# 免 Mod 匯入 rank-Yu 求解器

`importer/`（組件名 `Sts2Solver.Import`）從 ClrMD 程序快照組出 rank-Yu/sts2-solver 核心需要的 `Battle` 與 `Rules`，不在遊戲程序內執行任何程式碼。讀取器的 `/import` 端點回傳匯入結果、合法動作，以及核心模擬「現在結束回合」後的狀態。

組件名與上游 `src/Core` 的 `InternalsVisibleTo("Sts2Solver.Import")` 相同，才能使用上游匯入器所用的內部核心函式（如 `Piles.Append`、`Kernel.LogMonsterMove`）。

## 資料來源

| 部分 | 上游（遊戲內 Mod） | 本專案（遊戲外） |
| --- | --- | --- |
| 卡牌、藥水、遺物、能力的規範數值 | 在遊戲中複製標準模型並執行升級 | [規則目錄](rules-catalog.md)：上游 386 份凍結規則表合併 |
| 敵人行動狀態機與招式傷害 | 呼叫 `ToMutable`、`SetUpForCombat` | 規則目錄的敵人變體，以生命上限範圍挑選 |
| 敵人靜態整數、AI 狀態、開場能力層數 | 讀原生模型 | ClrMD 讀欄位；計算型屬性讀不到時沿用目錄值並在 `notes` 標示 |
| 根事實（能量上限、遭遇槽、樓層、模式、身分內容旗標） | `RulesCompiler.Compile` | `LiveRules` 依同樣規則從記憶體與 `Registry` 推出 |
| 卡牌閉包（可生成的卡與卡池） | `AddGeneratedCardClosure` | 同一演算法，數值取自目錄 |
| 戰鬥狀態（手牌、牌堆、能力、遺物計數、9 條亂數、歷史計數） | `BattleImporter`、`NativeHistory` | `LiveBattle`、`LiveHistory` 以 ClrMD 讀同名欄位 |

`Rules` 的部分旗標（例如愚者機制、回合結束仍在手中的效果）由規則表裡出現哪些卡牌推得，所以卡牌表只放根的閉包，不放整份目錄。

## 目前明確拒絕的局面

匯入失敗時回傳原因，不給出半成品建議：多人、非出牌階段、非標準模式、恐懼領域、目錄沒有的敵人（18 種，含 Toadpole）、會召喚其他敵人的敵人、附魔或苦難卡、充能球、召喚物、愚者機制、小數傷害卡、歷史課，以及上游有私有狀態投影但尚未移植的能力與遺物（清單見 `importer/LiveBattle.cs`）。

## 驗證

v0.113.1、進階 2、鐵甲戰士，第 4 層兩隻 Corpse Slug 戰第 1 回合（57/80 HP，未出牌）：

1. `/import` 匯入成功，列出 11 個合法動作。核心預測直接結束回合後：玩家 51 HP、獲得 2 層虛弱、左右敵人下一招分別為鞭打（3×2）與吞噬（8）。
2. 在遊戲中直接結束回合，畫面為 51/80、虛弱 2、意圖 3×2 與 8。
3. 再次 `/import`，與預測的 `Battle` 逐欄比對：405 個欄位中 402 個相同（含抽牌順序、手牌、敵人行動記錄、能力與 9 條亂數）。3 個差異是上游在新根刻意歸零的累計帳本（`CumulativeHpLost`、`PlayerUnblockedDamageEvents`、`VitalSparkNormalizedAmount`），見上游 `docs/import.md` §3.1。
