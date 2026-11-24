# NAVI — 상주형 개인 비서 (Local Personal Agent)

Claude API를 두뇌로 쓰는 Windows 상주형 개인 비서입니다. 트레이에 떠 있다가 단축키로 채팅창을
불러 요청하면, 필요할 때 Claude가 직접 도구를 호출해서 **실제로 PC를 조작**합니다 — 파일을
읽고 쓰고, 다른 앱 창을 찾아 클릭/타이핑하고, 화면을 직접 보고 판단합니다. 폰 앱에서도 같은
대화방을 보고 요청을 보낼 수 있습니다.

## 왜 만들었나

다운로드가 막힌 PDF 강의자료를 복사해야 하는 반복 작업에서 출발했습니다. "매번 사람이 화면을
보고 캡쳐-붙여넣기를 반복"하는 대신, Claude에게 도구를 쥐여주고 Windows UI Automation으로
PC를 직접 조작하게 만들면 이 과정을 포함한 더 많은 반복 작업을 자동화할 수 있겠다는 생각으로
시작했습니다.

## 핵심 기능

- **M1: PDF 다운로드 제한 우회 복제** — Chrome에 띄운 PDF를 페이지 단위로 캡쳐해서 Polaris
  Office 프레젠테이션에 그대로 재구성합니다. Windows UI Automation 탐색 병목을 찾아내
  페이지당 처리 속도를 20배 가까이 끌어올렸고, 그리드 기반 이미지 비교로 붙여넣기 결과를
  원본과 대조해 손상/중복을 자동 감지합니다.
- **도구 호출 에이전트 루프** — Claude가 상황에 맞춰 직접 고르는 도구 모음: 셸 명령 실행(허용
  폴더 화이트리스트), 파일 읽기/쓰기/수정, 임의의 창 UIA 트리 탐색/클릭/타이핑, 접근성 트리가
  없는 앱을 위한 화면 직접 보기(vision) 경로까지 갖췄습니다.
- **Claude API 프롬프트 캐싱 최적화** — 멀티라운드 도구 호출에서 대화 히스토리가 매 라운드
  캐시 없이 통째로 재전송되던 문제를 찾아 롤링 캐시 breakpoint를 적용, 반복 작업의 입력 토큰을
  수만 → 한 자릿수로 줄였습니다.
- **카카오톡 파일 전송 자동화** — 접근성 트리가 거의 비어 있는 레거시 데스크톱 앱을 UIA/MSAA
  레벨까지 직접 조사해서, 검색부터 파일 선택까지 결정적 코드로 처리하고 타이밍에 민감한 마지막
  확인 단계만 모델이 화면을 보고 판단하는 하이브리드 전략을 적용했습니다.
- **폰 ↔ PC 실시간 연동** — Firestore로 PC와 Android 앱이 같은 대화방을 공유합니다. PC가
  지금 어떤 작업을 하고 있는지(예: "화면 보는 중...")까지 실시간으로 동기화해서, 폰에서도
  요청이 실행 중인지 바로 확인할 수 있습니다.
- **Android 클라이언트** — Jetpack Compose로 만든 채팅 UI. 세션별 대화 기록, 실시간 타이핑
  인디케이터, 다크모드를 지원합니다.

## 아키텍처

```
┌─────────────────────────┐        Firestore        ┌──────────────────────────┐
│   Windows 상주 앱         │◄───────────────────────►│   Android 클라이언트        │
│   (Tray + 채팅창)         │   세션/메시지/실행 상태    │   (Jetpack Compose)       │
└───────────┬─────────────┘                          └──────────────────────────┘
            │
┌───────────▼─────────────┐
│   Orchestrator            │   사용자 메시지 → Claude 호출 → 도구 실행 → 반복
│   (도구 호출 루프)          │
└───────────┬─────────────┘
            │
    ┌───────┴────────────────────────────────┐
    │                                         │
┌───▼──────────────────┐          ┌───────────▼─────────────┐
│ 범용 PC 제어 도구        │          │  전용 결정적 도구            │
│ run_command / files_* │          │  M1 (Polaris PDF 복제)    │
│ gui_input / see_screen│          │  kakao_send_file          │
└───────────────────────┘          └───────────────────────────┘
```

## 기술 스택

| 영역 | 기술 |
|---|---|
| PC 상주 앱 | C# / .NET 8, WinForms, FlaUI(UI Automation) |
| 개인 비서 두뇌 | Anthropic Claude API (Messages API, 도구 호출, 프롬프트 캐싱) |
| 폰 ↔ PC 연동 | Firebase Firestore, Firebase Auth |
| Android 클라이언트 | Kotlin, Jetpack Compose, Material 3 |
| 보조 도구 | Node.js (이미지 → PDF 조립) |

## 프로젝트 구조

```
LocalAssistant/
  src/LocalAssistant.App/      WinForms 트레이 앱 + 채팅창 UI
  src/LocalAssistant.Cli/      CLI 진입점 (도구 설정, 진단, 비-GUI 테스트 실행)
  src/LocalAssistant.Engine/   Orchestrator, 도구 모음, Claude/Firestore 클라이언트
  pdf-duplicator-core-tool/    이미지 → PDF 조립 Node.js 유틸
android/                       Jetpack Compose 폰 클라이언트
assistant-design/              설계 문서 (아키텍처, 의사결정 기록)
firestore.rules                Firestore 보안 규칙 (사용자별 uid 범위 제한)
```

## 보안 설계

- API 키/인증 정보는 저장소에 포함되지 않습니다 — Windows DPAPI로 암호화해서 로컬
  `%LocalAppData%`에만 저장합니다.
- PC는 Firestore에 서비스 계정(Admin)으로 접근하고, 폰 앱은 Firebase Auth로 인증한 뒤 자기
  uid 문서만 읽고 쓸 수 있도록 `firestore.rules`로 제한되어 있습니다.
- `run_command` 도구는 사전에 등록한 폴더 화이트리스트 밖에서는 실행을 거부합니다.
