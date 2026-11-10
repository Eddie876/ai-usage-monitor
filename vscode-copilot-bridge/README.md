# AI 額度 Copilot Bridge

本機使用的 VS Code 擴充套件。手動執行「AI 額度：連接 VS Code 的 GitHub 帳號」後，使用 VS Code GitHub 登入狀態向 GitHub 查詢 Copilot 額度，每分鐘更新。僅將帳號名稱、用量和時間寫到本機 `%LOCALAPPDATA%\AiUsageWidget\copilot-usage.json`；不寫入 token。

GitHub 的用量端點不是公開穩定介面，可能受公司政策、帳號方案或介面變更影響。確認顯示的 GitHub 帳號與 VS Code 的 Copilot 帳號相同。需保持 VS Code 開啟。可執行「AI 額度：中斷 Copilot 連接」。
