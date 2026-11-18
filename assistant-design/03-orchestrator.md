# 자연어 개인 에이전트 (Core Orchestrator) — 설계도 (v0.1)

> 전제: [DESIGN.md](./DESIGN.md)에서 이미 정했던 "Core Orchestrator (Brain)" 레이어를
> 구체화하는 문서. [M1](../LocalAssistant) (크롬 PDF → Polaris PPTX 자동화, git tag `M1`)이
> 이 설계의 첫 번째 Tier 2 "스킬"이 된다. **아직 코드 없음 — 설계만.**

결정된 전제 (이번 라운드):
- **입력 방식**: 전역 단축키 팝업 (작은 입력창이 떠서 자연어 명령 입력)
- **판단 두뇌**: Claude API 연동 (Anthropic Messages API, tool use)

---

## 1. 목표

사용자가 단축키를 눌러 "크롬에 열린 PDF를 PPT로 만들어줘" 같은 자연어 한 줄을 입력하면,
Claude가 (a) 이미 만들어둔 전용 스킬을 쓸지 (b) 범용 UI 조작 도구를 조합해서 즉석으로
처리할지 판단해서 실행하고, 결과를 알려주는 상주형 에이전트.

## 2. 전체 흐름

```
[전역 단축키] → [입력 팝업] → 사용자 자연어 입력
                                   │
                                   ▼
                     ┌─────────────────────────┐
                     │   Orchestrator (루프)      │
                     │  Claude Messages API 호출   │
                     │  tool_use 올 때까지/끝날 때까지 │
                     └──────────┬──────────────┘
                                │ tool_use
                                ▼
                     ┌─────────────────────────┐
                     │      Tool Registry        │
                     │  (아래 3번 참고)             │
                     └──────────┬──────────────┘
                                │ tool_result
                                ▼
                     (Claude에게 결과 돌려주고 다음 턴 요청 — 반복)
                                │
                                ▼ end_turn
                     [결과 토스트/팝업으로 표시] + [감사 로그 기록]
```

핵심: **하나의 사용자 요청 = 하나의 "에이전트 루프"**. 루프 안에서 Claude가 도구를 여러 번
연달아 호출할 수 있음 (예: 창 목록 보기 → 그 중 하나 캡쳐 → 버튼 클릭 → 결과 확인). 요청 간
기억(대화 맥락)은 v1에서는 없음 (단순성 우선, 필요해지면 나중에 추가).

## 3. Tool Registry — Claude에게 뭘 쥐어줄 것인가

두 계층으로 나눔. **이 구분 자체가 안전장치**다 — "위험한 건 LLM이 판단해서 피하길
기대"하는 게 아니라, 애초에 그런 동작을 하는 도구 자체를 레지스트리에 안 넣는다.

### 3.1 전용 스킬 (고수준, 이미 검증된 파이프라인)

| 도구 이름 | 매핑 | 비고 |
|---|---|---|
| `convert_chrome_pdf_to_pptx` | `PolarisLauncher` + `PdfToPptxJob.Run` (M1) | 입력: 크롬 창 제목 힌트(선택). 이미 끝까지 검증됨 |

새 반복 작업이 생기면 이 레이어에 하나씩 추가 (예: 나중에 "블로그 포스팅" Tier 1 커넥터가
생기면 여기 합류).

### 3.2 범용 원시 도구 (저수준, Tier 2 UIA 엔진 재사용)

스킬이 없는 낯선 요청도 처리할 수 있게 하는 즉석 조합용 도구:

| 도구 이름 | 매핑 | 설명 |
|---|---|---|
| `list_windows` | `WindowLister` | 현재 떠 있는 창 이름 목록 (읽기 전용) |
| `dump_window` | `UiaTreeDumper` | 특정 창의 UI 구조(버튼/텍스트 등) 텍스트로 보기 (읽기 전용) |
| `screenshot_window` | `WindowPrintCapture` | 창을 이미지로 캡쳐해서 Claude에게 그대로 보여줌 (포커스 무관, 읽기 전용) |
| `click_element` | `ButtonClicker` | 이름으로 버튼/링크 Invoke (실제 마우스 클릭 아님 — 포커스 무관) |
| `set_text_value` | (신규, ValuePattern 래퍼) | 입력창에 `SetValue`로 텍스트 주입 (키 입력 시뮬레이션 아님) |

네 개는 이미 M1에서 만든 모듈 그대로 재사용, `set_text_value`만 `ChromePdfViewer.GoToPage`에서
썼던 `ValuePattern.SetValue` 패턴을 일반화해서 신규 작성.

→ `click_element`/`set_text_value`가 "실제 마우스/키보드 시뮬레이션이 아니라 UIA 네이티브
호출"이라는 제약은 그대로 유지 — 이게 게임 중에도 안전하게 동작하게 만든 핵심 원칙이었으니
범용 도구에도 똑같이 적용.

### 3.3 절대 안 넣는 것 (v1)

- 임의 쉘 명령 실행, 임의 키 입력 시뮬레이션(SendKeys 전체)
- 파일/데이터 삭제
- 결제, 금융 앱 조작
- 다른 사람에게 메시지 전송 (카카오톡 등)

이런 게 필요해지면 "범용 도구로 즉석 처리"가 아니라, 그 하나만을 위한 좁고 명시적인 전용
스킬(3.1 레이어)로 따로 설계/검토 후 추가. DESIGN.md 4.6의 화이트리스트 정신을 "도구
레지스트리 자체를 좁게 유지"로 구현.

## 4. 안전장치 (DESIGN.md 4.6 구체화)

| 장치 | 구현 |
|---|---|
| 킬 스위치 | 트레이 메뉴 "전체 중지" → 실행 중인 루프의 `CancellationToken` 즉시 취소 |
| 감사 로그 | 요청·도구 호출·결과를 로컬 JSONL에 append-only 기록 (`%LocalAppData%\LocalAssistant\audit.log`) |
| 화이트리스트 | 3.3절처럼 "도구 레지스트리에 없음"으로 구조적으로 강제 (LLM 신뢰 안 함) |
| 드라이런 모드 | 트레이 체크박스 ON 시, 각 도구가 실제 실행 대신 "이 도구를 이 입력으로 불렀을 것" 로그만 남기고 가짜 성공 반환 |
| 루프 폭주 방지 | 한 요청당 tool_use 라운드 최대 15회 캡 (01번 문서의 "runaway loop" 위험 대응) |
| 동시 실행 금지 | 루프 진행 중 새 단축키 입력은 거부하고 "처리 중" 표시 (v1은 큐잉 없음 — 단순함 우선) |

## 5. API 키 보관

최초 1회 설정 창에서 Anthropic API 키 입력 → Windows DPAPI(`ProtectedData.Protect`,
현재 사용자 범위)로 암호화해 로컬 파일에 저장. 평문 저장/로그 출력 금지.

## 6. 컴포넌트별 구현 스케치

- **Hotkey + 팝업**: 트레이 앱(WinForms) 안에 `RegisterHotKey` 전역 단축키, 작은 테두리 없는
  Topmost 텍스트박스. Enter = 제출, Esc = 취소.
- **Orchestrator**: `HttpClient`로 Anthropic Messages API 호출, `tool_use` 블록 파싱 →
  Tool Registry에서 이름으로 찾아 `Execute` → `tool_result`로 돌려보내는 루프.
  모델: `claude-sonnet-5-5` 기본값 (계획 정확도 우선, 필요시 추후 비용 최적화).
- **Tool Registry**: `ITool { Name, Description, JsonSchema InputSchema, Execute(JsonElement) }`
  인터페이스. 각 스킬/원시도구가 이걸 구현하며 기존 `LocalAssistant.Engine` 클래스를 그대로
  감쌈 — 엔진 코드 재작성 없음.
- **결과 표시**: 토스트 알림 또는 입력 팝업 자리에 결과 텍스트 표시.

## 7. 단계별 빌드 로드맵

1. **Step 1**: 단축키 + 팝업 + 도구 없이 Claude 호출만 → 검증: 질문 입력하면 답 나옴 (배관 확인)
2. **Step 2**: Tool Registry + `convert_chrome_pdf_to_pptx` 하나만 연결 → 검증: "크롬 PDF를
   PPT로 만들어줘"라고 자연어로 말하면 M1 파이프라인이 실제로 끝까지 돈다
3. **Step 3**: 범용 원시 도구 4~5개 추가 → 검증: 전용 스킬이 없는 낯선 작업(예: "메모장
   열고 '테스트'라고 쓰고 저장해줘")을 Claude가 원시 도구 조합만으로 즉석 처리하는지 확인
4. **Step 4**: 안전장치 UI(킬 스위치/감사 로그 뷰어/드라이런 토글) 트레이에 정리

## 8. 코딩 전에 정해야 할 미결정 사항

- [ ] `set_text_value`가 다룰 "입력창" 범위 — Chrome/Polaris 외 임의 앱까지 일반화할지, 아니면
      당장은 이미 검증된 두 앱으로 한정할지
- [ ] 드라이런 모드 기본값 (새 설치 시 ON으로 시작해서 사용자가 직접 끄게 할지)
- [ ] 감사 로그 보관 기간/용량 정책 (무한 누적 vs 롤링)
- [ ] 다중 모니터 환경에서 팝업 표시 위치 규칙
