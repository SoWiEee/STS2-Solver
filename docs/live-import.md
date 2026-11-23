# 免 Mod 匯入 rank-Yu 求解器

`importer/`（組件名 `Sts2Solver.Import`）從 ClrMD 程序快照組出 rank-Yu/sts2-solver 核心需要的 `Battle` 與 `Rules`，不在遊戲程序內執行任何程式碼。讀取器的 `/import` 端點回傳匯入結果、合法動作，以及核心模擬「現在結束回合」後的狀態。

組件名與上游 `src/Core` 的 `InternalsVisibleTo("Sts2Solver.Import")` 相同，才能使用上游匯入器所用的內部核心函式（如 `Piles.Append`、`Kernel.LogMonsterMove`）。

## 資料來源

| 部分 | 上游（遊戲內 Mod） | 本專案（遊戲外） |
| --- | --- | --- |
| 卡牌、藥水、遺物、能力的規範數值 | 在遊戲中複製標準模型並執行升級 | [規則目錄](rules-catalog.md)：上游 386 份凍結規則表合併 |
| 敵人行動狀態機與招式傷害 | 呼叫 `ToMutable`、`SetUpForCombat` | 規則目錄的敵人變體，以生命上限範圍挑選；目錄沒有的 18 種改由 `LiveMonsterRules` 從記憶體讀狀態機 |
| 目錄外敵人的計算型數值（進階相關的格擋、力量、初始生命） | 呼叫 getter | `reader/data/monster-statics.json`：REA 讀 `sts2.dll` 的 getter CIL（`ldc.i4 等級; ldc.i4 高; ldc.i4 低; call GetValueIfAscension`），由 `tools/monster-statics/extract.mjs` 產生 |
| 敵人靜態整數、AI 狀態、開場能力層數 | 讀原生模型 | ClrMD 讀欄位；計算型屬性改查 REA 數值表（依本局進階等級取值），兩者都沒有才沿用目錄值並在 `notes` 標示 |
| 進階等級相關的招式傷害與次數 | 呼叫 `DamageCalc`、`Repeats` | 目錄只提供狀態機結構；每招的傷害與次數重讀記憶體：常數閉包直接讀值，綁定到敵人 getter 的委派依 REA 數值表取值 |
| 根事實（能量上限、遭遇槽、樓層、模式、身分內容旗標） | `RulesCompiler.Compile` | `LiveRules` 依同樣規則從記憶體與 `Registry` 推出 |
| 卡牌閉包（可生成的卡與卡池） | `AddGeneratedCardClosure` | 同一演算法，數值取自目錄 |
| 戰鬥狀態（手牌、牌堆、能力、遺物計數、9 條亂數、歷史計數） | `BattleImporter`、`NativeHistory` | `LiveBattle`、`LiveHistory` 以 ClrMD 讀同名欄位 |

`Rules` 的部分旗標（例如愚者機制、回合結束仍在手中的效果）由規則表裡出現哪些卡牌推得，所以卡牌表只放根的閉包，不放整份目錄。

## 目前明確拒絕的局面

匯入失敗時回傳原因，不給出半成品建議：多人、非出牌階段、非標準模式、恐懼領域、目錄外敵人中需要遊戲內求值的部分（條件分支、由 getter 計算的攻擊傷害或次數、非常數的隨機權重）、會召喚其他敵人的敵人、附魔或苦難卡、充能球、召喚物、愚者機制、小數傷害卡、歷史課，以及上游有私有狀態投影但尚未移植的能力與遺物（清單見 `importer/LiveBattle.cs`）。

## 整場戰鬥搜尋（`/solve`）

`importer/LiveSearch.cs` 用上游的 `BeamSolver` 搜尋到戰鬥結束，網頁「整場戰鬥搜尋」面板逐回合顯示路線與預測的戰鬥結束 HP。

- 評分表：卡牌、能力、藥水的評分項目與根無關，取自上游 Golden 的凍結請求（`reader/data/score-template-*.br`，一般戰與第三幕 Boss 各一份）。根相關欄位依上游 `ScoreCompiler` 用即時狀態重算：生命上限、敵人數、戰後回血遺物、Boss 生命策略（預設「推進優先」：第一、二章 Boss 計入章節回血，終局 Boss 只看存活）與四個排名基線。
- 未使用上游的剪枝下界（只影響速度）；搜尋不使用藥水；束寬 300、時限 10 秒。
- 第三幕雙 Boss 的第二個 Boss 尚未區分，帶骨肉遺物會拒絕。

## 驗證

v0.113.1、進階 2、鐵甲戰士，第 4 層兩隻 Corpse Slug 戰第 1 回合（57/80 HP，未出牌）：

1. `/import` 匯入成功，列出 11 個合法動作。核心預測直接結束回合後：玩家 51 HP、獲得 2 層虛弱、左右敵人下一招分別為鞭打（3×2）與吞噬（8）。
2. 在遊戲中直接結束回合，畫面為 51/80、虛弱 2、意圖 3×2 與 8。
3. 再次 `/import`，與預測的 `Battle` 逐欄比對：405 個欄位中 402 個相同（含抽牌順序、手牌、敵人行動記錄、能力與 9 條亂數）。3 個差異是上游在新根刻意歸零的累計帳本（`CumulativeHpLost`、`PlayerUnblockedDamageEvents`、`VitalSparkNormalizedAmount`），見上游 `docs/import.md` §3.1。
4. 第 2 回合（51/80）執行 `/solve`：2.3 秒、14,042 個節點找到勝利路線，預測戰鬥結束時 40/80。照路線在遊戲中出牌，下一回合重新搜尋得到與原路線剩餘部分相同的步驟；戰鬥實際以 40/80 結束（燃燒之血再回 6）。
5. 第 5 層 Seapunk（目錄外）：攻擊傷害由記憶體中的常數閉包讀得（Sea Kick 11、Spinning Kick 2×4），`BubbleBlock` 等由 REA 數值表依進階 2 取值。直接結束回合的預測與遊戲逐欄比對 352 欄中 349 欄相同，差異同上述 3 個帳本。`/solve` 3.2 秒找到勝利路線、預測 34/80；照路線出牌（含 Brand 選擇消耗 Defend），中途重新搜尋得到相同剩餘步驟，戰鬥實際以 34/80 結束。

6. 第 7 層精英 Skulking Colony（目錄內，Hardened Shell）：所有招式傷害由記憶體依進階 2 重讀，沒有沿用目錄值。`/solve` 0.7 秒預測勝利、戰鬥結束 11/80；路線標出兩次 Brand 的選擇（消耗 Defend、Strike）。照路線打完 6 回合，實際以 11/80 結束。第 2 回合重新搜尋得到另一條預測 10/80 的路線：束搜尋不保證每次從不同根都找到同一最佳解。

REA 數值表（`reader/data/monster-statics.json`）涵蓋全部 108 種敵人的 522 個整數 getter；`tools/monster-statics/extract.mjs` 只辨識「常數」與「依進階等級二選一」兩種 getter，其他形式不收錄。

目錄外的其他 17 種敵人尚未實機遇到，能否匯入取決於它們的狀態機是否只用常數閉包。
