package com.navi.assistant

import androidx.lifecycle.ViewModel
import com.google.firebase.auth.FirebaseAuth
import com.google.firebase.firestore.FieldValue
import com.google.firebase.firestore.FirebaseFirestore
import com.google.firebase.firestore.ListenerRegistration
import com.google.firebase.firestore.Query
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

// 04-android-windows-bridge.md: PC와 같은 conversations/{uid}/sessions/{sessionId}/messages
// 구조를 실시간으로 구독/쓰기만 하는 씬 클라이언트. Claude 호출/도구 실행은 전부 PC에서만
// 일어나고, 세션 목록은 PC의 FirestoreBridge가 올려주는 sessions 컬렉션을 그대로 따른다.
data class ChatMessage(val role: String, val text: String, val source: String)
data class SessionItem(val id: String, val title: String, val activity: String? = null)
data class PcStatus(val connected: Boolean, val detail: String?)

class ChatViewModel : ViewModel() {
    private val auth = FirebaseAuth.getInstance()
    private val firestore = FirebaseFirestore.getInstance()

    private var sessionsListener: ListenerRegistration? = null
    private var messagesListener: ListenerRegistration? = null
    private var statusListener: ListenerRegistration? = null
    private var hasPickedInitialSession = false

    private val _messages = MutableStateFlow<List<ChatMessage>>(emptyList())
    val messages: StateFlow<List<ChatMessage>> = _messages.asStateFlow()

    private val _sessions = MutableStateFlow<List<SessionItem>>(emptyList())
    val sessions: StateFlow<List<SessionItem>> = _sessions.asStateFlow()

    private val _currentSessionId = MutableStateFlow<String?>(null)
    val currentSessionId: StateFlow<String?> = _currentSessionId.asStateFlow()

    private val _pcStatus = MutableStateFlow<PcStatus?>(null)
    val pcStatus: StateFlow<PcStatus?> = _pcStatus.asStateFlow()

    private val _isSignedIn = MutableStateFlow(auth.currentUser != null)
    val isSignedIn: StateFlow<Boolean> = _isSignedIn.asStateFlow()

    private val _authError = MutableStateFlow<String?>(null)
    val authError: StateFlow<String?> = _authError.asStateFlow()

    init {
        if (auth.currentUser != null) startListening()
    }

    fun signIn(email: String, password: String) {
        auth.signInWithEmailAndPassword(email, password)
            .addOnSuccessListener {
                _authError.value = null
                _isSignedIn.value = true
                startListening()
            }
            .addOnFailureListener { e -> _authError.value = e.message ?: "로그인 실패" }
    }

    fun send(text: String) {
        val uid = auth.currentUser?.uid ?: return
        val sessionId = _currentSessionId.value ?: return
        if (text.isBlank()) return

        val doc = hashMapOf(
            "role" to "user",
            "text" to text,
            "source" to "phone",
            "handled" to false,
            "timestamp" to FieldValue.serverTimestamp()
        )
        firestore.collection("conversations").document(uid)
            .collection("sessions").document(sessionId)
            .collection("messages").add(doc)
    }

    // 사이드바 "+ 새 대화" - 빈 세션을 하나 만들고 바로 전환한다. PC의 FirestoreBridge가
    // 이 세션 문서를 보고 자기 쪽에도 같은 id로 빈 세션을 만들어 사이드바를 맞춘다.
    fun newChat() {
        val uid = auth.currentUser?.uid ?: return
        val sessionRef = firestore.collection("conversations").document(uid)
            .collection("sessions").document()
        val doc = hashMapOf(
            "title" to "새 대화",
            "updatedAt" to FieldValue.serverTimestamp()
        )
        sessionRef.set(doc).addOnSuccessListener {
            switchSession(sessionRef.id)
        }
    }

    fun selectSession(id: String) {
        if (id == _currentSessionId.value) return
        switchSession(id)
    }

    private fun switchSession(id: String) {
        _currentSessionId.value = id
        _messages.value = emptyList()
        listenToMessages(id)
    }

    private fun startListening() {
        val uid = auth.currentUser?.uid ?: return

        sessionsListener?.remove()
        sessionsListener = firestore.collection("conversations").document(uid)
            .collection("sessions")
            .orderBy("updatedAt", Query.Direction.DESCENDING)
            .addSnapshotListener { snapshot, error ->
                if (error != null) {
                    android.util.Log.e("ChatViewModel", "sessions listen failed", error)
                    return@addSnapshotListener
                }
                val list = snapshot?.documents?.map { doc ->
                    SessionItem(doc.id, doc.getString("title") ?: "새 대화", doc.getString("activity"))
                } ?: emptyList()
                _sessions.value = list

                // 처음 들어왔을 때만: 세션이 있으면 가장 최근 것을, 없으면 새로 만든다.
                if (!hasPickedInitialSession) {
                    hasPickedInitialSession = true
                    val first = list.firstOrNull()
                    if (first != null) switchSession(first.id) else newChat()
                }
            }

        statusListener?.remove()
        statusListener = firestore.collection("conversations").document(uid)
            .addSnapshotListener { snapshot, error ->
                if (error != null) {
                    android.util.Log.e("ChatViewModel", "status listen failed", error)
                    return@addSnapshotListener
                }
                val statusMap = snapshot?.get("pcStatus") as? Map<*, *>
                _pcStatus.value = statusMap?.let {
                    PcStatus(
                        connected = it["connected"] as? Boolean ?: false,
                        detail = it["detail"] as? String
                    )
                }
            }
    }

    private fun listenToMessages(sessionId: String) {
        val uid = auth.currentUser?.uid ?: return
        messagesListener?.remove()
        messagesListener = firestore.collection("conversations").document(uid)
            .collection("sessions").document(sessionId)
            .collection("messages")
            .orderBy("timestamp", Query.Direction.ASCENDING)
            .addSnapshotListener { snapshot, error ->
                if (error != null) {
                    android.util.Log.e("ChatViewModel", "messages listen failed", error)
                    return@addSnapshotListener
                }
                val list = snapshot?.documents?.mapNotNull { doc ->
                    val role = doc.getString("role") ?: return@mapNotNull null
                    val text = doc.getString("text") ?: return@mapNotNull null
                    val source = doc.getString("source") ?: "pc"
                    ChatMessage(role, text, source)
                } ?: emptyList()
                _messages.value = list
            }
    }

    // 재설계된 사이드바 하단 계정 영역의 로그아웃 버튼에서 호출. 리스너를 전부 끊고
    // 상태를 초기화해서 다음에 다른 계정으로 로그인해도 이전 계정의 대화가 안 섞이게 한다.
    fun signOut() {
        sessionsListener?.remove()
        messagesListener?.remove()
        statusListener?.remove()
        sessionsListener = null
        messagesListener = null
        statusListener = null
        hasPickedInitialSession = false

        auth.signOut()

        _messages.value = emptyList()
        _sessions.value = emptyList()
        _currentSessionId.value = null
        _pcStatus.value = null
        _authError.value = null
        _isSignedIn.value = false
    }

    override fun onCleared() {
        sessionsListener?.remove()
        messagesListener?.remove()
        statusListener?.remove()
        super.onCleared()
    }
}
