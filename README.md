# NearWebsiteAuditAgent

.NET 10 確定性網站安全與基本 SEO audit Worker。輸入一個公開 HTTP(S) URL，輸出 JSON／Markdown；可處理 NEAR Agent Market 已分配任務。沒有 AI、瀏覽器自動化、前端或資料庫依賴。

## 本地執行

安裝 global.json 指定的 SDK 10.0.403；runtime 最低 10.0.14，允許後續同 minor patch。本次工作區亦可使用已校驗的 .tools/dotnet/dotnet.exe（Windows）或 .tools/dotnet-linux/dotnet（Linux）取代下列 dotnet；ignored SDK 不隨 repo 交付。

在 repo root 建立 request.json：

~~~json
{"url":"https://example.com/"}
~~~

~~~powershell
dotnet restore NearWebsiteAuditAgent.slnx --locked-mode
dotnet run --project src/NearWebsiteAuditAgent.Worker -- --Execution:Mode Local --Execution:InputFile request.json --Storage:Root .audit-data/local
~~~

輸出檔名為 audit GUID 的 .json／.md，完成 log 只列檔名及狀態；Local 不需 NEAR token。exit 0=完整執行、2=部分可測、3=拒絕、4=失敗、5=取消。完整執行仍可能有 findings；exit 0 不代表網站沒有問題。

## 範圍與安全

22 個固定 checks：HTTPS／certificate／negotiated TLS、security headers、cookie attributes、HTTP upgrade／redirect、title／description／H1／canonical／robots meta、robots.txt、sitemap.xml 與有限同 origin link probes。

只解析提交頁面，不執行 JS、不登入、不送變更請求、不爬全站。transport 驗證公網 IP、釘定實際 socket、逐 redirect 重驗，關閉 proxy／cookie jar／certificate downloads。不可測為 NotTested，部分報告不產生通過 grade；公開報告不含 URL path/query 或 cookie values。

## NEAR 與恢復

官方 REST poll → start → audit → submit。預設不接受正式付費任務；先設定 test assignment allowlist。成功提交並讀回 submitted/accepted 後，保存同一 Agent 的最小驗證 metadata；正式工作還需顯式開啟 AcceptPaidAssignments。

每個 revision 有 journal、原子報告及提交意圖。submit 回應不明時進 AwaitingReconciliation；重啟先讀回平台，不盲重送。不明 brief／revision 停 NeedsReview。

本次未使用真實 token 或平台測試任務；實際 NEAR lifecycle 與付費接單仍待驗證。[操作與部署](docs/operations.md) 說明設定、憑證、限制及人工恢復。

## 專案與驗證

- Contracts：wire DTO／enum／JSON。
- Core：checks、score、orchestrator、network policy、ports。
- Infrastructure：HTTP/TLS、AngleSharp、reports、atomic files、NEAR adapter。
- Worker：設定、console／Windows Service lifetime、Local／Market execution。
- Tests：xUnit、受控 HTTP/TLS、OpenSSL AIA egress、journal／restart／contract cases。

~~~powershell
dotnet build NearWebsiteAuditAgent.slnx --no-restore
dotnet test tests/NearWebsiteAuditAgent.Tests/NearWebsiteAuditAgent.Tests.csproj --no-build
~~~

測試不需外部 API 或 token。Linux 需 openssl；Windows 使用 Git for Windows 的 usr/bin/openssl.exe。只啟動並清理自己的 local fixture server／暫存目錄。

[規格索引](docs/README.md) · [實作計畫](docs/website-audit-mvp/implementation-plan.md) · [fixture 報告](docs/samples/README.md)
