# Direct API verification — 2026-10-29

範圍：使用者於直接呼叫 POST /audit 方案後指示「實作」的增量。基線為 main merge commit 6a76daa（tree 與原實作 3ec7b92 相同）；驗證涵蓋未提交 Api project／兩份 HTTP tests、共用 ValidateAudit、project／package locks 及文件。當前程式與設定的 SHA-256 見 direct-api-input-manifest.sha256。舊 MVP 的 input-manifest.sha256 保留為歷史證據，不以舊測試宣稱新 API 通過。

環境：Windows x64 Release、Ubuntu WSL2 Debug；SDK 10.0.403／runtime 10.0.14，重用既有校驗過的 ignored .tools SDK。沒有真實 API key、市場 token、外部 Agent 或目標部署環境。測試 key 為明示 fixture；僅 Tests 透過 DI 注入，production 沒有 auth-disable 設定。

## Executed verification

Windows dotnet 指 .tools/dotnet/dotnet.exe；Linux 指 .tools/dotnet-linux/dotnet，獨立 --artifacts-path .tools/linux-build。

| Check | Command / method | Actual result |
| --- | --- | --- |
| Restore | dotnet restore NearWebsiteAuditAgent.slnx --force-evaluate；Linux --locked-mode | 六 projects 成功；只新增鎖定的 Mvc.Testing 10.0.14／TestHost／DependencyModel 與 Api project 依賴，既有套件版本未升級 |
| Windows | dotnet test NearWebsiteAuditAgent.slnx -c Release --no-restore | 200 passed、0 failed、0 skipped；含全部 165 舊 tests＋35 API cases |
| Linux | dotnet test tests/NearWebsiteAuditAgent.Tests --artifacts-path .tools/linux-build --no-restore | 200 passed、0 failed、0 skipped |
| Build | dotnet build NearWebsiteAuditAgent.slnx -c Release --no-restore | 0 warnings、0 errors |
| Format | dotnet format NearWebsiteAuditAgent.slnx whitespace --verify-no-changes --no-restore | exit 0 |
| Publish | dotnet publish src/NearWebsiteAuditAgent.Api -c Release --no-restore --self-contained false -o .tools/api-publish | 成功；framework-dependent ASP.NET Core API artifact；未部署 |
| Dependencies | dotnet list NearWebsiteAuditAgent.slnx package --vulnerable --include-transitive | 當次 feed 對六 projects 無已知 vulnerable package；不是未來保證 |
| Kestrel smoke | WebApplicationFactory.UseKestrel(0)，真實 loopback socket、實際 production pipeline | 兩 OS：anonymous 401、authorized 200、oversized 413、no-store；factory 釋放自己的 server |

原先外部背景 process smoke 指令被 automatic approval review 以 blocked by policy 拒絕，沒有實際啟動。改用框架支援的受控 in-process Kestrel 驗證真實 TCP，而非宣稱外部 process／正式服務啟動已通過。CLI key 拒絕另直接執行真實 entry point，exit 1；沒有啟動 server。從 shell 注入真實 key 的正式部署仍未驗收。

## TDD / acceptance mapping

- 初始專案／HTTP route 尚未存在：先加入 DirectApiTests；補最小 host 使 tests 可執行後，12 個 cases 因 404 失敗，再實作 endpoint／認證／guards。沒有以空殼 route 當完成。
- 新增邊界 regression：未認證 unknown route 錯誤耗用 rate budget、非 terminal runner result 錯誤以 AuditResult 回 500；兩案先 RED，再分別以 anonymous no-limiter 與 safe ProblemDetails 修正。
- 第一版測試誤把既有 origin URL 序列化預期為無 trailing slash；依現有 Redaction 契約修正測試，未變更 production URL 格式。

| AC | Evidence |
| --- | --- |
| AC19 | Authorized_HTTP_call：真實 orchestrator／22 checks／既有 JSON enum schema；HTTP_API_reaches：API→ObservationCollector→SafeTargetTransport→受控 HTTPS server，憑證有效、無 URL secret echo |
| AC20 | 無／錯／重複 key、query／cookie 不採用；startup key／rate validation、CLI key 真實 entry point；external HTTP＋偽 forwarded headers 426；unknown／duplicate fields、非法 UTF-8、encoding／media type、已知／未知長度 body size；10 秒 slow-body 408 |
| AC21 | rate 429／Retry-After、並行立即 429、取消後 permit 可重用、host stopping 取消；unknown route 無法耗用已認證 rate budget |
| AC22 | rejected 422、failed 502、timeout 504、partial 200、report cap 503、internal／nonterminal 500 ProblemDetails；sentinel response＋captured logs、no-store；真實 Kestrel／兩 OS 舊 Worker regressions |

## Readability and compliance

Host／DI／authentication／HTTP exception boundary／endpoint 分檔，endpoint 只處理 HTTP mapping 並呼叫 IAuditRunner；Core 沒有 ASP.NET reference。DI 與 limiter 組裝集中保持可讀，沒有為形式行數額外拆 abstraction。ValidateAudit 為既有驗證規則的抽取，舊 Worker 相關 tests 與全部 regressions 通過。無 model／database／market state 改變。

父代理依 spec-compliance-reviewer 逐項核對八份 owning docs、R10／AC19–22、HTTP statuses、資料流、金鑰來源、rate／concurrency／timeout／report limits 及無 UI／webhook／storage 決策：本地與受控驗證範圍 compliant。公開 HTTPS domain／reverse proxy／部署帳號／外部 Agent 呼叫未驗收；D05／AC16 仍 pending。這些是部署／市場缺口，不以測試替代。

## Independent code review

獨立 read-only reviewer 檢查完整增量（含未追蹤 Api／tests），另跑原 33 API tests 通過；回報一項 P2：啟動驗證允許所有可列印 ASCII，但部分字元（逗號／不成對引號）不符合 Bearer header parser，造成可啟動卻永遠 401。

先加入真實 startup regression，觀察到 key 含逗號仍成功啟動（RED：1 failed／1 passed）。因 API 尚未發布且任意 ASCII 不是使用者要求，選擇限縮為標準 Bearer token 字元：英數字與 -._~+/，可帶尾端 =，總長 32–256，禁止純 padding。同步修正 README／operations／API contract。正向 HTTP case 確認標準符號及尾端 padding 可認證；兩個 focused tests GREEN。

同一 reviewer 再做 read-only 覆核，另跑新增兩項 tests：2 passed／0 failed；原 P2 關閉，沒有剩餘可操作 findings。父代理重核對受影響 key 契約／設定／AC，scope compliance 仍成立。正式 HTTPS／proxy／服務帳號與實際 key 注入屬未判定部署範圍。最終兩 OS 各 200 tests 通過，受影響 gate 的證據已更新。
