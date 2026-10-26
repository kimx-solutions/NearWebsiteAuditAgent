# Implementation verification — 2026-10-26

## Scope and identity

Review scope：本 worktree 的全部新增／修改內容，包括未追蹤的 src、tests、docs、project/config/workflow files；不是只有 git diff 的 .gitignore。Base HEAD = 23f1e1cfe1d2c34d0f49dccc045a538c4b50c68d，尚未 commit／push。受測輸入 manifest 見 input-manifest.sha256；修改相依 source／contract 後須更新結果與 manifest。

SDK 10.0.403／runtime 10.0.14，Windows 10.0.26200 x64；Linux 為本機 Ubuntu WSL2（kernel 6.6.87.2），獨立 SDK 與 artifacts path。兩個 SDK 依官方 release metadata SHA512 校驗後安裝在 ignored .tools。測試不需市場 token，亦未呼叫真實 start／submit。

## Executed checks

所有命令自 repo root 執行。下列 dotnet 在 Windows 指 .tools/dotnet/dotnet.exe；Linux 指 .tools/dotnet-linux/dotnet，經 WSL Ubuntu 執行。

| Check | Command / method | Actual result |
| --- | --- | --- |
| Restore / lock | dotnet restore NearWebsiteAuditAgent.slnx；後續 Windows／Linux --locked-mode | 成功；各 project 的 packages.lock.json 已保存 |
| Windows Release | dotnet build NearWebsiteAuditAgent.slnx -c Release --no-restore | 0 warnings、0 errors |
| Windows xUnit | dotnet test tests/NearWebsiteAuditAgent.Tests/NearWebsiteAuditAgent.Tests.csproj -c Release --no-build | 165 passed、0 failed、0 skipped |
| Linux xUnit | dotnet test tests/NearWebsiteAuditAgent.Tests/NearWebsiteAuditAgent.Tests.csproj --artifacts-path .tools/linux-build --no-restore | 165 passed、0 failed、0 skipped |
| Formatting | dotnet format NearWebsiteAuditAgent.slnx whitespace --verify-no-changes --no-restore | exit 0 |
| Dependencies | dotnet list NearWebsiteAuditAgent.slnx package --vulnerable --include-transitive | 五個 projects 於本次 NuGet feed 查核無已知易受攻擊套件；非未來無漏洞保證 |
| Windows CLI | 實際執行 Worker DLL：restricted input／missing mode／CLI token | exit 3／1／1；restricted 產生兩種 report；log 無 sentinel secret |
| Linux CLI | 實際 Worker DLL + restricted input | exit 3；root mode 0700、report mode 0600；log 無 sentinel secret |
| Public smoke | Windows Release Worker，https://example.com/，duration 30s、attempts 12、links 2 | exit 0、Succeeded；18 tested / 22 planned，其餘為合法 N/A；score 44（規則版本的 findings，不是網站合規結論）；JSON／Markdown 生成 |
| Samples | opt-in PipelineTests export | docs/samples 中兩個實際 fixture outputs；不含 cookie values／query sentinel |

本次曾修正 WSL smoke wrapper 的 native quoting：最終將可檢查的 shell script 存於 ignored .tools 再執行；上表只記錄修正後的真實 CLI 結果。

## TDD and regression evidence

- Foundation：功能殼層時 45 failed／1 passed，實作 parser／IP policy／scoring 後 46 passed。
- Checker batch：42 tests 因未實作失敗，加入真正純判定／AngleSharp extraction 後通過。
- Transport：10 expected failures，之後驗證 DNS 混合、connected peer、rebinding、redirect、chunked limit、共享預算與 cancellation。
- Pipeline／files：10 expected failures，完成 orchestrator／collector／atomic store／reports 後通過。
- Market：15 expected failures，完成實際 REST client、完整 schema fixtures、journal、recovery、revision 後通過。
- Regression：target POST/PUT/DELETE、空 canonical、submit success 的超大 response、start 消耗 SLA、DOM complexity 先觀察失敗再修正。
- Contract edges：setting key／timezone-free timestamp／unknown CSP directive／malformed frame source 先失敗再修正。
- Config／bounded evidence／semaphore 從失敗測試驗證到通過；未以 source-string assertion 或 mock 自己的回傳當功能證據。

原始長 logs 保留在 ignored .tools；此文件只列命令／結果與適用範圍，避免 log 複製成多份進度表。

## AC coverage

| AC | Evidence |
| --- | --- |
| AC01 | PipelineTests、ExecutionTests、兩格式 JSON／Markdown；另有 example.com 實際 public smoke |
| AC02–AC03 | FoundationTests IP／URI matrix、TransportTests 實際 socket recorder；兩 OS OpenSslAiaTests |
| AC04 | TlsTransportTests 的有效／過期／hostname mismatch native TLS；pure negotiated version／invalid certificate tests |
| AC05 | CheckEngineTests header cases、unknown grammar 與 framing precedence |
| AC06 | TransportTests redirect／rebinding／private destination／same-origin，PipelineTests HTTP upgrade |
| AC07 | Cookie matrix、Expires comma、persistent/nonpersistent/unknown evidence、HttpOnly applicability；pipeline／JSON／Markdown sentinel leak checks |
| AC08–AC09 | HTML／sitemap DTD／合法大 XML cases、bounded link sample／HEAD fallback；實際 fixture requests 並確認不取 script |
| AC10–AC11 | Foundation score／coverage、bounded findings、CheckEngine partial link outcome／NotTested／N/A |
| AC12–AC13 | Transport／Budget／EdgeCase／Pipeline cancellation、request／body／HTML cap、後續工作仍能執行 |
| AC14–AC15 | MarketClientTests 完整官方 envelope、auth／retry／unknown submit；MarketWorkTests persistence／restart／lost response／revision；FileStore／Retention tests、start 前 bytes／file slots gate、virtual poll-deadline regression |
| AC16 | **未執行真實平台測試**；隔離測試不替代。D05 待維運者提供受管 token、test assignment、費用／mutation 授權 |
| AC17 | 兩 OS build／tests／console smoke 與依賴盤點；SCM／systemd 安裝或實際 service account 部署未執行 |
| AC18 | operations、實際 fixture outputs、lock／capacity／retention tests、Linux permissions smoke；部署環境 walkthrough 仍待維運者 |

Windows native Schannel fixture 曾因 ephemeral private key 失敗，僅在 Tests 重載自身 PFX 修正，沒有改 trust-all。AIA 另用自有 OpenSSL server 隔離 Windows SERVER 的 chain retrieval，兩 OS 均確認 CLIENT 不向 internal AIA server 發送 HTTP；未改全域 certificate trust store。

## P4 readability result

檢查 Core orchestrator／checks、transport／retry、file repository／reports、Worker lifetime 與市場 use case。MarketWorkProcessor 移至 Core，經 IReportContentReader port 讀 payload，拆出 StartWorkAsync／RunWorkAsync／SubmitReadyAsync；Core 只引用 Contracts。狀態與外部 API／DTO 行為不變；16 項受影響 focused tests 通過後，完整 155 tests 於兩 OS 再驗證。

transport 的逐跳 loop 保留在同一 resource lifetime 中，避免拆分後失去 cancellation／disposal／DNS guard 的可見性；未為形式限制拆出無意義抽象。

## P8 compliance scope

本地 audit／reports／安全邊界與 NEAR adapter 的隔離可驗證範圍，符合 owning slice contract；實作澄清已寫回 domain/data/API。無 production fake／NotImplemented path、無前端、DB 或 AI 依賴。UI／兩種 UX 階段依既有範圍不適用。

D05、真實平台 AC16、SCM/systemd deployment、遠端 CI 與使用者最終驗收尚未執行。因此這是可審查實作與本地驗證完成，**不是整個 MVP 的正式市場驗收完成或已發布**。

## P9 review

獨立 reviewer 對完整工作樹（含 untracked source）執行 read-only review，另重跑原 155 tests；回報五項 P2，均經本地驗證成立。修正前先觀察回歸測試失敗：

| Finding | Observed RED | Implemented correction / GREEN |
| --- | --- | --- |
| poll cycle 提早取消 audit | virtual timer 在 audit 中觸發 poll deadline，token 錯誤取消 | polling token 在交給 processor 前 dispose；host cancellation 與 audit/SLA 獨立，無 real-time sleep 的 regression 通過 |
| framing wildcard host 假通過 | https://*、http://*:8080、*:* 三例錯誤 Passed | 無限制 host 判 Failed；*.example.com 保持通過 |
| 合法大 sitemap 假失敗 | >2 MiB 合法 XML 錯誤 Failed | parser 配合 transport 8 MiB hard cap；超限為 NotTested，不作 invalid XML 扣分 |
| Cookie 期限分類未輸出 | check evidence 為空，兩個 regression 失敗 | JSON／Markdown 均含 safe name、flags、lifetime、purpose=unknown；每 check 三筆＋省略摘要；無 values |
| start 前未保留容量 | bytes／file slots 不足仍會 start | 單 instance／循序 worker 取得 report pair＋journal／temp headroom lease；兩個 fault tests 通過；釋放空間後可續跑 |

額外檢查 temporary-path 例外仍釋放 write semaphore，file count 包含 atomic temporary slot，retention 於 poll 前先執行。這是 app quota 保護；外部磁碟耗盡或硬體 I/O 並無虛構保證。

修正後 Windows Release 與 Linux Debug 各 165 passed／0 failed／0 skipped；Windows build 0 warnings／0 errors，format verify exit 0。第二次獨立 read-only 覆核另跑 focused Release regressions：54 passed／0 failed／0 skipped，五項均關閉，未留 P0–P3 findings；同意本地／隔離範圍的 P9。修改後重核對 spec、資料／report 契約與 AC 映射，P8 的相同範圍結論仍成立。

Declined-to-judge 項目 disposition：weak TLS sweep／online revocation 依原範圍明確排除；authentication-cookie 推論按批准政策保留 unknown；真實 NEAR AC16／服務安裝／遠端 CI 列為外部驗證缺口。沒有藉審查將其標記已完成。
