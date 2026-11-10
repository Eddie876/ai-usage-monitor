# AI Usage Monitor

Windows 懸浮視窗，用來查看 Claude、Codex 和 GitHub Copilot 的剩餘額度及重置倒數。視窗可拖曳、置頂，失焦時會半透明；關閉按鈕會將程式隱藏至系統匣。

## 安裝與使用

1. 從 Releases 下載 Windows x64 壓縮檔並解壓縮，保留所有檔案，執行 `AiUsageWidget.exe`。程式自帶 .NET 執行環境。
2. 登入 Claude Desktop 和 Codex Desktop。Copilot 需另外安裝隨 Release 提供的 VS Code 擴充套件。
3. 在 VS Code 執行「Extensions: Install from VSIX...」，選擇 `ai-usage-widget-copilot-bridge.vsix`；重新載入後執行「AI 額度：連接 VS Code 的 GitHub 帳號」並依提示授權。使用 Copilot 資料時需保持 VS Code 開啟。

在系統匣右鍵可立即更新、複製 Claude 連線狀態、設定開機啟動或結束程式。請先結束舊版再執行新版。

## 資料來源與限制

- **Claude**：讀取目前 Windows 使用者的 Claude Desktop 本機資料，使用 Windows 保護的 OAuth 登入資料查詢用量，並核對帳號與組織；token 在記憶體使用，不寫入其他檔案或記錄。若即時查詢失敗，會使用本機用量紀錄，重置時間可能未知。方案標籤目前固定顯示 Team，並非自動偵測。
- **Codex**：透過 Codex app-server 查詢帳號額度。
- **Copilot**：VS Code 擴充套件使用已授權的 GitHub 登入查詢 premium requests，每分鐘將帳號名稱、用量及時間寫入 `%LOCALAPPDATA%\AiUsageWidget\copilot-usage.json`，不寫入 token。請確認授權的 GitHub 帳號是使用 Copilot 的帳號。

Claude 與 Copilot 的部分資料依賴非公開穩定的介面，可能因服務更新、帳號方案或組織政策而失效。額度與重置時間僅供參考；沒有來源資料時不推算重置時間。若 Claude 查詢失敗，可透過系統匣複製不含憑證的診斷狀態；其中包含本機帳號與路徑，分享前請自行檢查。

## 從原始碼建置

需要 Windows 與 .NET 10 SDK：

```powershell
dotnet publish .\AiUsageWidget.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o .\publish
```

執行 `publish\AiUsageWidget.exe`，並保留同目錄的 DLL。測試：

```powershell
dotnet run --project tests\Checks.csproj -c Release
node --test vscode-copilot-bridge\tests\quota.test.js
```

本專案不設定或執行 GitHub Actions。
