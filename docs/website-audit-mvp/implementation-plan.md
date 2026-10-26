---
title: Website Audit Agent MVP 實作計畫
document_type: implementation-plan
status: in_progress
module_code: website-audit
slice_id: website-audit-mvp
module_spec_package: docs/website-audit-mvp
req_anchor: https://github.com/kimx-solutions/NearWebsiteAuditAgent/issues/1
eng_anchor: docs/website-audit-mvp/implementation-plan.md
work_state: awaiting_external_validation
last_completed_step: P9
next_step: P10
---

# 實作計畫

本文件是唯一執行進度。來源與待決事項由 [requirement](requirement.md) 擁有；開工已批准；本地實作與驗證已進行，D05 及外部驗收獨立保留。

## 實作順序

使用者確認 → `vertical-slice-implementer` 全程統籌 → design-time UX（不適用）→ backend → backend readability → frontend（不適用）→ test completion → implemented-ui UX（不適用）→ spec compliance → code review。

本包起初由 requirement-normalizer 產出；使用者明確開工後由 vertical-slice-implementer 統籌，各適用技能完成後回到本計畫更新證據與缺口。

## Progress

- [x] P0 規格審閱與實作範圍確認
  - status: done
  - result: 2026-10-26 使用者於本對話指示「開始實作」；採用已列保守建議，D05 真實市場測試仍待憑證與明確授權。
  - references: [來源與 R01–R09](requirement.md#範圍與需求錨點)、[D01–D07](requirement.md#待決事項)、[AC](acceptance-criteria.md#observable-criteria)
  - exit_criteria: 使用者明確批准規格與可執行範圍；記錄確認訊息與尚待決定項的阻塞範圍。只回覆文件路徑不算批准。
  - dependency: 無；如果只批准獨立本地／離線工作，明列授權邊界，市場與實網路徑繼續受 gate 限制。

- [x] P1 Slice kickoff／contract alignment
  - skill: `vertical-slice-implementer`
  - status: done
  - result: 無既有程式／測試／AGENTS／profile；owning slice 正確；NEAR live OpenAPI SHA-256 與來源摘錄一致；必要技能可用，適用階段不變。
  - references: [requirement](requirement.md)、[flow](prototype-flow.md)、[UI 判定](ui-spec.md#applicability)、[data](data-flow.md)、[domain](domain-spec.md)、[API](api-contract.md)、[AC](acceptance-criteria.md)
  - exit_criteria: P0 有有效授權；重新讀目前 AGENTS／指定 profile／工作樹／Issue 版本，確認 owning slice、規則版本及適用技能可用；核對 live OpenAPI 與來源摘錄差異；未決事項已分配 owner／阻塞項；沒有必需 skill 缺失或假 production stub。
  - dependency: P0；不因換技能重新詢問已批准的同範圍。

- [x] P3 後端實作（以 contract／安全依賴拆工）
  - skill: `backend-feature-development`
  - status: done
  - references: [data](data-flow.md)、[domain](domain-spec.md)、[API](api-contract.md)、[AC01–AC18](acceptance-criteria.md#observable-criteria)
  - exit_criteria: 下列 P3a–P3e 在已批准範圍完成，DTO／domain／Worker 邊界成立；所有 production 外連走安全 transport；市場提交具持久化與安全對帳；沒有未追溯欄位或空白結果掩蓋缺口。
  - dependency: P1；P2 不適用。這是 .NET Worker，不為套 skill 強加 ASP.NET API、EF Core、CQRS 或資料庫；如技能讀取後發現必要 repo gate 缺失，先明列差異並處理，不默默略過。

  - [x] P3a Foundation／contracts／pure model
    - skill: `backend-feature-development`
    - status: done
    - references: [model](domain-spec.md#model)、[DTO](api-contract.md#local-contract)、AC01、AC10–AC11、AC17
    - exit_criteria: planned .NET 10 solution／五 projects／DI／設定驗證／clock seam、DTO serialization、outcomes／score pure functions 有可驗證路徑，尚未允許無安全 transport 的實網 audit。
  - [x] P3b Safe transport／budget／persistence
    - skill: `backend-feature-development`
    - status: done
    - references: [boundary](data-flow.md#network-boundary)、[budgets](data-flow.md#resource-budgets)、[repositories](domain-spec.md#repositories)、AC02–AC03、AC06、AC12–AC13、AC15
    - exit_criteria: DNS 與 socket 綁定、每跳驗證、TLS 隱含 fetch 邊界、shared budgets、取消及 journal 原子寫入／單 instance lock 可驗；安全測試先成立才接 public target；D02／D04 相依決策已確認。
  - [x] P3c Checkers／orchestrator／local reports
    - skill: `backend-feature-development`
    - status: done
    - references: [checks](domain-spec.md#checks)、[scoring](domain-spec.md#scoring)、[report](api-contract.md#report)、AC01、AC04–AC13
    - exit_criteria: 每個 check 有真實 observation 路徑、明確 NotTested／N/A、safe evidence 與 recommendation；JSON／Markdown 同源；D03 已確認或僅實作已批准子集合，不偽稱完整 MVP。
  - [x] P3d NEAR polling／start／submit／recovery
    - skill: `backend-feature-development`
    - status: done
    - references: [NEAR API](api-contract.md#near-api)、[retry](api-contract.md#retry-and-delivery)、[assignment state](domain-spec.md#assignment-state)、AC14–AC16
    - exit_criteria: 官方 mapping、pagination、ownership／capacity、strict input、start-before-work、bounded retry、deadline、durable pending submission／restart／revision 成立；D01／D06／D07 已解除其相依路徑；無 token 時只有明示的 local mode，不以假的 MarketClient 接單。
  - [x] P3e 操作與部署文件／samples
    - skill: `backend-feature-development`
    - status: done
    - references: [operator](ui-spec.md#operator-surface)、[storage](data-flow.md#persistence-and-secrets)、AC17–AC18
    - exit_criteria: setup／environment keys／limits／Windows／Linux 啟停／token／journal 修復／retention 文件可走查；samples 來自 fixture，標示非實際 public audit；Docker 只有已選擇才新增。

- [x] P4 後端可讀性檢查
  - skill: `refactor-backend-methods`
  - status: done
  - references: P3 的實際 diff（planned）、[domain invariants](domain-spec.md)、[boundary](data-flow.md#network-boundary)
  - exit_criteria: 已審閱 orchestrator、transport、retry／recovery、parsers 的責任與方法可讀性；記錄重構或無須修改的結論；必要修改保持契約，重驗相依範圍。
  - dependency: P3；小方法／低風險不是省略檢查理由。

- [x] P6 測試補全與驗證證據
  - skill: `project-test-completion`
  - status: done
  - references: [所有 AC](acceptance-criteria.md#observable-criteria)、[evidence 規則](acceptance-criteria.md#evidence-rules)、[驗證](#verification)
  - exit_criteria: 每個 checker、contracts、SSRF／socket、redirect、取消／資源、durable retries／unknown submission 的必要 unit／integration 已執行；兩 OS 的必要證據可追溯；AC16 真實測試與其他未驗項目明列，不以 mock 或單 OS 結果代替。
  - dependency: P4；P5 不適用；D05 只擋實際平台驗收，不能阻止獨立 local tests，但全 MVP 不得標完成。

- [x] P8 規格符合性檢查
  - skill: `spec-compliance-reviewer`
  - status: done
  - references: 本包八份規格、實際 diff／evidence（planned）、[writeback 政策](requirement.md#差異與-writeback)
  - exit_criteria: spec／flow／DTO／checks／AC／plan 一致；每個不適用理由仍成立；無未處理 blocker；需要變更需求的 delta 已明列並完成適用確認，沒有捏造 writeback 狀態。
  - dependency: P6 與適用驗證完成；P7 不適用。只做已批准子集合時明確限制 review 結論，不視為整體 MVP 合規。

- [x] P9 程式碼審查與關閉 findings
  - skill: `senior-code-reviewer`
  - status: done
  - result: 全工作樹獨立 review 的五項 P2 已以 RED→GREEN regression 修正；二次覆核 54 tests 通過，未留 P0–P3。最終完整兩 OS 各 165 tests 通過；僅批准本地／隔離實作範圍。
  - references: P8 compliance 結果、實際 diff／tests／evidence（planned）、[安全邊界](data-flow.md#network-boundary)、[assignment invariants](domain-spec.md#assignment-state)
  - exit_criteria: correctness／架構／資料／契約／安全／回歸等阻擋 findings 已解；修改使證據失效的範圍已重驗，再做受影響 compliance／review；本地／隔離範圍的必要 AC 有實際證據；外部驗收交 P10，不以 P9 代表全 MVP 結案。
  - dependency: P8；審查本身不授權 commit／push／PR／發布。

- [ ] P10 真實市場與部署驗收
  - status: pending
  - owner: 維運者／需求 owner 提供 D05 受管憑證、已授權 test assignment 與費用／mutation 範圍；實作者其後執行並留證。
  - references: AC16–AC18、[D05](requirement.md#待決事項)、[操作](../operations.md)、[證據](evidence/implementation-verification.md)
  - exit_criteria: 真實 NEAR poll→start→audit→submit→讀回結果與需用 failure／revision cases、目標部署 service account／啟停驗證、遠端 CI 與使用者驗收各自有證據；未滿足前整體 plan 維持 in_progress。
## Not applicable

| Stage | Skill／mode | 狀態與原因 |
| --- | --- | --- |
| P2 | `senior-ux-reviewer` / design-time | not-applicable；S1 排除 frontend／dashboard，無互動畫面或 prototype；[UI 判定](ui-spec.md#applicability) |
| P5 | `frontend-feature-development` | not-applicable；只有 .NET Worker、檔案與外部 REST client，無 React／瀏覽器 UI |
| P7 | `senior-ux-reviewer` / implemented-ui | not-applicable；無新頁面、layout、interaction、responsive 或 accessibility 變更，不捏造 rendered evidence |

P0/P1/P3/P4/P6/P8/P9 皆適用。已安裝技能以本會話 available skills 為依據；未來執行時仍須實際讀取並確認可用。任何必需 skill 缺失為 blocker，不能改列 optional。

## Test focus

- AC02–AC03、AC06、AC12：不只驗 parser；驗最終 socket、redirect、預算、TLS 外部抓取與 proxy 旁路，使用公開允許控制組防止「全部拒絕」假通過。
- AC04–AC11：每個 rule 的正／負／不可測／不適用、cookie parsing、HTML/XML 安全、score 邊界／排序／coverage；不以巨大快照取代可觀察契約測試。
- AC13–AC15：取消、跨任務持續運作、分頁／schema 漂移、disk fault、remote commit 後 timeout、restart 與雙 instance。
- AC16–AC18：真實平台 lifecycle 與兩 OS 自架證據；憑證或授權未提供時保留未驗，不自行 self-hire 或接受付費工作。

## Planned outputs

以下 paths 已建立，evidence 與 deployment 範圍另行區分：

| Path | 工作／相容性邊界 |
| --- | --- |
| `NearWebsiteAuditAgent.slnx` | net10.0 solution，精確 SDK 10.0 patch 執行時由官方支援狀態選取並 pin，不在本輪猜版號 |
| `src/NearWebsiteAuditAgent.Contracts/` | DTO／wire enums／schema version；沒有 inbound controller |
| `src/NearWebsiteAuditAgent.Core/` | pure checks、score、orchestrator、ports、domain states |
| `src/NearWebsiteAuditAgent.Infrastructure/` | DNS/socket/TLS/HTTP／AngleSharp／JSON+Markdown writer／file repository／NEAR client |
| `src/NearWebsiteAuditAgent.Worker/` | Generic Host／DI／mode／configuration／poll loop／logging／shutdown |
| `tests/NearWebsiteAuditAgent.Tests/` | xUnit unit + controlled HTTP/TLS integration／schema／fault injection tests；loopback exception 僅 Tests seam |
| `docs/operations.md`、`docs/samples/` | 操作／部署與 fixture 生成報告；不得保存 token 或 fake live scan |
| `docs/website-audit-mvp/evidence/` | 實際執行後的證據，遵守 redaction；不預建通過結果 |

沒有既有 API／DB schema／migration 要相容；本地 journal 新版仍須尊重 pending reports 的恢復，不任意刪檔。production Docker、CI workflow 若新增，需記錄具體範圍與實際驗證，不能因本計畫出現便聲稱已配置。

## Verification

以下 solution／commands 已建立並執行本地適用驗證；CI workflow 已新增但遠端尚未跑。執行者須先核對實際 project 名稱、SDK 及新 AGENTS／workflow，再更新此表；不能把建立前失敗或無測試當作測試通過。

| 工作目錄 | 命令或方法 | 證據 |
| --- | --- | --- |
| repo root | `dotnet --info`；核對 pinned net10.0 SDK | SDK／OS／依賴支援狀態 |
| repo root | `dotnet restore NearWebsiteAuditAgent.slnx` | 真實還原結果，無 AI 依賴 |
| repo root | `dotnet build NearWebsiteAuditAgent.slnx --no-restore` | 五 project build 結果 |
| repo root | `dotnet test tests/NearWebsiteAuditAgent.Tests/NearWebsiteAuditAgent.Tests.csproj --no-build` | checker／transport／orchestrator／repository／adapter tests；若 SDK 採不同 runner，先記錄官方相容命令 |
| repo root | 按已完成 operations 文件執行 Local mode、Windows／Linux service start/stop／env override／recovery | AC01、AC13、AC17–AC18，兩 OS 分開 |
| 核准測試環境 | 官方支援 test assignment 的 poll→start→audit→submit→讀回狀態，含 revision／failure case | AC16；需 D05，無 token／授權就記未執行 |
| repo root | `git diff --check` 與實際 docs links／metadata 交叉核對 | 文件一致性；不取代 build／tests／外部驗收 |

## Gates and gaps

實作已授權；D05 仍 pending，其他決策採既有保守建議，影響範圍由 [requirement](requirement.md#待決事項) 單一維護。執行結果見下方紀錄；無規格階段的程式驗證結果可沿用。

保持 CI、安全與發布邊界：不自動 commit／push／PR／部署／listing／投標／發訊／花費。實作授權不含真實市場 mutation 或測試費用；子代理只有適用技能明確要求的最終獨立審查，不自行平行委派實作。

## Execution record — 2026-10-26

- Approval：使用者「開始實作」授權本包，採 D01/D02/D03/D04/D06/D07 建議，D05 open。
- Workspace：Codex managed worktree；保留原 E: checkout 未提交 docs，複製到新 workspace；無 commit/push。
- Preflight：HEAD 23f1e1c，無程式基線；NEAR schema 未變；採 backend-feature-development 與 test-driven-development 依計畫實作。
- Ruling：本計畫為唯一執行 ledger；本包不是 Superpowers commit/task-script 格式，依 vertical-slice 路由記錄實際結果，避免第二份進度台帳。
- Ruling：採官方當前 SDK 10.0.403／runtime 10.0.14，以 SHA512 校驗後安裝在 ignored .tools，不修改系統 SDK；NuGet 版本取官方 stable 並 pin。
- Ruling：report URL 只顯示 origin，resource 用短 hash 識別，避免 path/query 秘密；完整 URL 僅存受保護 journal。
- P3a RED：FoundationTests 45 failed / 1 passed，失敗因所測功能尚未實作的 NotImplementedException；序列化設定控制組通過。其後才加入 parser／IP classifier／score 行為。

有新 scope／contract／dependency／環境變動時，只失效受影響證據與下游 gate，保留有效歷史。每個完成項目才補 result／evidence；證據含 commit+工作樹差異或等價識別，不能只用 HEAD 代表未提交程式。沒有實際完成不要新增「實作結果」章節。

- P3a/P3b/P3c/P3d implementation: 本地 pipeline、22 checks、atomic files、market contracts／journal／restart／revision 已建立；151 項 Windows tests 通過，0 warnings build。Linux 102-test baseline 已通過，待對最終版本重驗。Stage evidence 將集中於 evidence/implementation-verification.md。
- Additional safeguards: 受控 test assignment allowlist＋同 Agent 的 durable validation metadata 及 AcceptPaidAssignments 開關；既有 D05 gate 仍未解除。metadata 無網站／token，保留供 gate 使用。
- Boundary corrections: 限定 target GET/HEAD、空 canonical 不算有效、submit 成功但 response 不可讀仍為 unknown、start 耗盡 SLA 不開新 audit、DOM complexity 50,000 markup 上限與 bounded evidence。已觀察到失敗的 regression tests 後才修正。

- P3e: README、operations、SHA-pinned CI、package locks、真正 fixture reports 已產出；SCM/systemd 只提供部署文件，未操作系統服務。
- P4: use case 移 Core 並拆分 start/audit，16 項 focused tests 通過，後續兩 OS 全部 155 項通過。
- P6: 155/155 Windows Release + 155/155 Linux、CLI smoke、Linux 0700/0600、public example.com、dependency audit、format 已核對；詳細結果見 evidence/implementation-verification.md。
- P8: 本地／隔離實作範圍 compliant；未以 fake／mock 替代 AC16。完整市場驗收仍受 D05 阻擋。

- P9: 五項 review finding 已修正並覆核；Windows Release／Linux Debug 各 165 tests 通過，0 warnings／errors；格式與新檔 whitespace 檢查通過。完整結果及 declined-to-judge disposition 見 [evidence](evidence/implementation-verification.md#p9-review)。
- P10 next: 本地實作可交付；真實市場／部署／CI／使用者驗收未執行，整包保持 in_progress，不要求重做已通過的 unchanged local gates。
