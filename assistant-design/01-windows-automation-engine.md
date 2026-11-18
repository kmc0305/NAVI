# Windows 범용 자동화 엔진 — 상세 설계 (Phase 1 구현 대상)

> [DESIGN.md](./DESIGN.md)의 "Tier 2 — API 없는 범용 UI 자동화" 부분만 떼어내어
> 가장 먼저 구현할 수 있도록 상세화한 문서. LLM 브레인/스케줄러/트레이 앱 등 상위
> 레이어는 이 엔진이 안정화된 뒤에 그 위에 얹는다.

## 0. 목표

API가 없는 **임의의 Windows 데스크톱 앱**을 대상으로, 창을 찾고 그 안의 버튼/입력창/
텍스트를 읽고 조작할 수 있는 범용 엔진을 만든다. 이게 완성되면 상위의 "브레인"이 이
엔진을 도구로 삼아 어떤 작업이든 수행할 수 있게 된다.

## 1. 레이어 구조

```
Layer 4   Skill Library          앱별로 미리 검증된 루틴 ("카카오톡.메시지전송" 등)
Layer 3   Perception-Action Loop 처음 보는 앱/작업을 위한 AI 기반 범용 루프
Layer 2   Action Primitives DSL  Click / TypeText / Invoke / WaitFor / GetText ...
Layer 1   Element Selector       창/컨트롤을 선언적으로 지정하고 탐색·대기
Layer 0   OS Interop             UIA COM, Win32 API, SendInput, 스크린샷
```

실행 우선순위: **Layer 4(검증된 스킬이 있으면 그대로 재생) → 없을 때만 Layer 3(AI가
화면을 보고 직접 판단)**. 빠르고 안정적인 경로를 항상 먼저 시도하고, 처음 보는
상황에서만 비용이 드는 AI 루프로 떨어진다.

## 2. Layer 0 — OS Interop

- **UIA**: `FlaUI.Core` / `FlaUI.UIA3` — 원시 UIAutomationClient COM API를 감싼 래퍼
- **창 열거/제어**: `FlaUI.Core.Application`, `Desktop.Windows`
- **저수준 입력**: Win32 `SendInput` — UIA 패턴이 작동하지 않는 레거시/커스텀 컨트롤의
  최후 수단
- **스크린샷**: `Graphics.CopyFromScreen` 또는 `Windows.Graphics.Capture`
- **DPI/멀티모니터 보정**: 좌표 기반 동작은 반드시 모니터별 스케일 팩터를 반영해야
  정확한 위치에 클릭됨

## 3. Layer 1 — Element Selector

요소를 선언적으로 지정하는 셀렉터. 예:

```
window(title~="카카오톡")
  .descendant(controlType="Edit", name="메시지 입력")
```

- 매칭 기준: `AutomationId`(언어 변경에도 안정적이라 최우선), `Name`, `ControlType`,
  텍스트 정규식, 상대 위치(N번째 자식)
- **대기/재시도가 기본 동작**: 창/요소가 늦게 나타나는 경우가 대부분이므로
  `WaitFor(selector, timeout=5s, pollInterval=200ms)`를 모든 호출에 내장
- 매칭 실패 시 그 순간의 UIA 트리 스냅샷을 로그로 남겨 왜 못 찾았는지 바로 확인 가능

## 4. Layer 2 — Action Primitives

| Primitive | 동작 |
|---|---|
| `Click(selector)` | `InvokePattern` 우선 시도 → 실패 시 좌표 클릭으로 폴백 |
| `TypeText(selector, text)` | `ValuePattern.SetValue` 우선 → 안 되면 포커스 후 `SendInput` |
| `Toggle(selector)` | 체크박스/토글 스위치 |
| `GetText(selector)` | 현재 텍스트/값 읽기 |
| `WaitFor(selector, timeout)` | 요소 등장/소멸 대기 |
| `ListChildren(selector)` | 하위 트리 나열 (탐색·디버깅용) |
| `Screenshot(region?)` | 비전 폴백 경로용 캡처 |

모든 Primitive의 실행 결과(성공/실패/소요시간)는 Audit Log에 기록.

## 5. Layer 3 — Perception-Action Loop (범용 폴백)

처음 다루는 앱이거나 Layer 4에 저장된 스킬이 없을 때 쓰는, "AI가 직접 화면을 보고
판단"하는 루프:

1. 현재 포커스 창의 UIA 트리를 **압축 텍스트로 직렬화**(role/name/상태만 추려서 —
   전체 트리를 그대로 주면 토큰 낭비) + 필요 시 스크린샷 첨부
2. LLM에게 "목표: {자연어 명령}, 현재 상태: {트리/스샷}, 사용 가능한 Primitive 목록"
   전달
3. LLM이 다음 액션 1개를 선택 (tool-use 포맷)
4. 실행 → 결과 관찰 → 2~4 반복
5. 종료 조건: 목표 달성 판단 / 최대 스텝 수 초과 / 동일 상태 반복(루프 감지) 중 하나

이 루프는 느리고 토큰 비용이 들기 때문에, **성공한 시퀀스는 Layer 4 스킬로 자동
승격**해서 다음엔 LLM 없이 바로 재생한다 (학습형 캐시 구조).

## 6. Layer 4 — Skill Library

- 앱별 자주 쓰는 루틴을 선언적 스크립트(JSON/YAML)로 저장: 셀렉터 + Primitive 시퀀스
- 예: `skills/kakaotalk/send_message.yaml`
  ```yaml
  steps:
    - click: { window: "카카오톡", name: "{chat_name}" }
    - type:  { selector: "message_input", text: "{message}" }
    - click: { selector: "send_button" }
  ```
- Layer 3이 처음 성공시킨 시퀀스를 이 라이브러리로 "승격"하는 파이프라인 포함

## 7. 비전 폴백 (UIA 트리가 부실한 앱)

Electron/커스텀 렌더링 기반 앱(카카오톡 PC 포함 가능성 높음)은 UIA 요소가 거의 노출
안 될 수 있음. 이 경우:

- 스크린샷 + OCR(`Windows.Media.Ocr` 또는 Tesseract)로 텍스트 위치 탐색
- 아이콘/버튼은 템플릿 매칭(OpenCV) 또는 사전 등록된 기준 이미지로 좌표 탐색
- 좌표 클릭은 DPI 스케일 보정 필수

## 8. 구현 순서 (이 엔진만 떼어서)

1. **Step 1** — FlaUI로 메모장/계산기 같은 표준 Win32 앱에서 "창 탐색 → 텍스트 입력
   → 버튼 클릭" 왕복 성공 (가장 쉬운 대상으로 파이프라인 동작 증명)
2. **Step 2** — Element Selector DSL + 대기/재시도 로직 정식화
3. **Step 3** — UIA 트리 → 압축 텍스트 직렬화기 (LLM 입력용) 작성
4. **Step 4** — Perception-Action Loop를 Claude API에 연결, 써보지 않은 임의 앱
   1개로 "AI가 직접 조작"하는 것 검증
5. **Step 5** — 성공 시퀀스 → Skill Library 승격 파이프라인 구현
6. **Step 6** — 비전 폴백(OCR) 추가, UIA 트리가 부실한 앱 1개로 검증 (카카오톡이
   좋은 시험대)

## 9. 알아둬야 할 함정

- **권한 레벨 불일치**: 비서 프로세스가 일반 권한인데 대상 앱이 관리자 권한으로
  뜨면 UIA가 그 창을 건드릴 수 없음(UIPI 제한) → 비서도 상황에 따라 관리자 권한
  실행이 필요할 수 있음
- **포커스 탈취**: 자동화 중 사용자가 다른 창을 쓰면 충돌 가능 → 작업 중 포커스
  잠금 또는 입력 중 알림 표시 고려
- **다국어/상태 변화**: 언어 변경 시 `Name` 텍스트 매칭이 깨짐 → 가능하면
  `AutomationId`(언어 무관) 우선 매칭
- **루프 폭주 방지**: Layer 3이 같은 실수를 반복하면 스텝 제한 + "상태 변화 없음"
  감지로 강제 종료

## 10. 1단계 완료 기준 (여기까지 되면 "엔진 완성"으로 판단)

- [ ] 메모장 같은 표준 앱에서 Layer 0~2 왕복 동작
- [ ] 써보지 않은 임의 앱 1개를 Layer 3 루프로 조작 성공
- [ ] 성공한 시퀀스가 Layer 4 스킬로 저장되고, 재실행 시 LLM 호출 없이 재생됨
- [ ] UIA가 부실한 앱 1개에서 비전 폴백으로 동일 작업 성공
