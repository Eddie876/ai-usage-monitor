# AI 額度 Copilot Bridge

本機使用的 VS Code 擴充套件。手動執行「AI 額度：連接 VS Code 的 GitHub 帳號」後，使用 VS Code GitHub 登入狀態向 GitHub 查詢 Copilot 額度，每分鐘更新。僅將帳號名稱、用量和時間寫到本機 `%LOCALAPPDATA%\AiUsageWidget\copilot-usage.json`；不寫入 token。

從 0.1.1 版起，擴充套件會讀取桌面程式的「監控來源」設定。取消勾選 GitHub Copilot 會暫停額度查詢並保留 GitHub 授權。重新勾選後，等候下一次排程或在 VS Code 執行「AI 額度：重新整理 Copilot」即可恢復查詢。

GitHub 的用量端點不是公開穩定介面，可能受公司政策、帳號方案或介面變更影響。確認顯示的 GitHub 帳號與 VS Code 的 Copilot 帳號相同。需保持 VS Code 開啟。可執行「AI 額度：中斷 Copilot 連接」。
