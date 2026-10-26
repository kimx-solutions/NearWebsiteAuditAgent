# Fixture reports

JSON／Markdown 由 PipelineTests 的受控 HTTP/TLS fixture，經真正的 orchestrator、checks、score 與 ReportWriter 產生。沒有掃描同名公網站點。

fixture 包含有效 TLS、部分 security headers 缺漏、正常 cookie flags、基本 SEO、robots／sitemap、broken link 與 HEAD fallback。公開輸出省略 path/query 和 cookie values；不是 NEAR 真實交付證據。

明確 opt-in 重產（普通 tests 不寫 repo）：

~~~powershell
$env:NEAR_AUDIT_SAMPLE_DIR = (Resolve-Path docs/samples).Path
dotnet test tests/NearWebsiteAuditAgent.Tests/NearWebsiteAuditAgent.Tests.csproj --filter FullyQualifiedName~The_full_local_pipeline
Remove-Item Env:NEAR_AUDIT_SAMPLE_DIR
~~~

原始 GUID 檔名複製為 website-audit.fixture.json／website-audit.fixture.md；檔內 identity／timestamps 為實際 fixture 執行資料。
