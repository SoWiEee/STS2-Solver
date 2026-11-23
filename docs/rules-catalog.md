# 規則目錄涵蓋率

由 `tools/rules-catalog` 從 `vendor/sts2-solver/tests/Golden/data` 的凍結 RULE 表產生（rank-Yu/sts2-solver，MIT）。只合併標準模式；重新產生：`dotnet run --project tools/rules-catalog -c Release`。

| 項目 | 數量 |
| --- | ---: |
| 來源規則表（標準模式） | 386 |
| 略過的其他模式 | Dread 2、Blitz 1 |
| 卡牌 | 661 / 673 種（1537 個 id×升級）|
| 能力參數 | 16 / 16（只有定義了參數的能力需要；共 278 種能力） |
| 遺物參數 | 119 / 119（只有定義了參數的遺物需要；共 315 種遺物） |
| 藥水 | 68 / 68 |
| 卡池 | 31 |
| 藥水池變體 | 戰鬥內 2、戰鬥外 2（目錄取最常見者） |
| 敵人 | 90 / 108 種，175 個數值變體 |
| 驗證 | 98737 個來源值逐欄位比對：0 個不符，2 個已知的根相依差異 |

## 尚未涵蓋的敵人

`ARCHITECT`、`ASSASSIN_RUBY_RAIDER`、`AXE_RUBY_RAIDER`、`BRUTE_RUBY_RAIDER`、`CALCIFIED_CULTIST`、`CROSSBOW_RUBY_RAIDER`、`DAMP_CULTIST`、`FAKE_MERCHANT_MONSTER`、`FOSSIL_STALKER`、`HAUNTED_SHIP`、`MAWLER`、`MYSTERIOUS_KNIGHT`、`SEAPUNK`、`SEWER_CLAM`、`SLITHERING_STRANGLER`、`SLUDGE_SPINNER`、`TOADPOLE`、`TRACKER_RUBY_RAIDER`

## 敵人數值變體

同一敵人的變體通常對應不同進階等級（生命與傷害較高者為高進階），等 C4 用 REA 的 getter 公式確認對應關係。

| 敵人 | 變體 | 初始生命 |
| --- | ---: | --- |
| `AEONGLASS` | 1 | 512 |
| `AXEBOT` | 4 | 70、80、80、90 |
| `BATTLE_FRIEND_V1` | 1 | 75 |
| `BATTLE_FRIEND_V2` | 1 | 150 |
| `BATTLE_FRIEND_V3` | 1 | 300 |
| `BOWLBUG_EGG` | 2 | 21–22、23–24 |
| `BOWLBUG_NECTAR` | 2 | 35–38、36–39 |
| `BOWLBUG_ROCK` | 2 | 45–48、46–49 |
| `BOWLBUG_SILK` | 2 | 40–43、41–44 |
| `BYGONE_EFFIGY` | 2 | 127、132 |
| `BYRDONIS` | 1 | 90 |
| `CEREMONIAL_BEAST` | 2 | 252、262 |
| `CHOMPER` | 2 | 60–64、60–64 |
| `CORPSE_SLUG` | 3 | 25–27、25–27、25–27 |
| `CRUSHER` | 2 | 209、219 |
| `CUBEX_CONSTRUCT` | 2 | 65、70 |
| `DECIMILLIPEDE_SEGMENT_BACK` | 3 | 40–46、46–52、46–52 |
| `DECIMILLIPEDE_SEGMENT_FRONT` | 3 | 40–46、46–52、46–52 |
| `DECIMILLIPEDE_SEGMENT_MIDDLE` | 3 | 40–46、46–52、46–52 |
| `DEVOTED_SCULPTOR` | 2 | 162、172 |
| `ENTOMANCER` | 2 | 145、165 |
| `EXOSKELETON` | 8 | 24–28、24–28、24–28、24–28、26–30、26–30、26–30、26–30 |
| `EYE_WITH_TEETH` | 1 | 6 |
| `FABRICATOR` | 3 | 150、150、155 |
| `FAT_GREMLIN` | 1 | 13–17 |
| `FLAIL_KNIGHT` | 2 | 101、108 |
| `FLYCONID` | 1 | 51–53 |
| `FOGMOG` | 2 | 74、78 |
| `FROG_KNIGHT` | 2 | 191、199 |
| `FUZZY_WURM_CRAWLER` | 2 | 55–57、58–59 |
| `GAS_BOMB` | 1 | 7 |
| `GLOBE_HEAD` | 2 | 148、158 |
| `GREMLIN_MERC` | 1 | 47–49 |
| `GUARDBOT` | 2 | 16–20、17–21 |
| `HUNTER_KILLER` | 1 | 121 |
| `INFESTED_PRISM` | 2 | 161、171 |
| `INKLET` | 2 | 12–18、12–18 |
| `KIN_FOLLOWER` | 4 | 58–59、58–59、62–63、62–63 |
| `KIN_PRIEST` | 2 | 190、199 |
| `KNOWLEDGE_DEMON` | 2 | 379、399 |
| `LAGAVULIN_MATRIARCH` | 1 | 222 |
| `LEAF_SLIME_M` | 2 | 32–35、33–36 |
| `LEAF_SLIME_S` | 1 | 11–15 |
| `LIVING_FOG` | 1 | 80 |
| `LIVING_SHIELD` | 1 | 55 |
| `LOUSE_PROGENITOR` | 2 | 134–136、138–141 |
| `MAGI_KNIGHT` | 2 | 82、89 |
| `MECHA_KNIGHT` | 2 | 300、320 |
| `MYTE` | 2 | 61–67、64–69 |
| `NIBBIT` | 6 | 42–46、42–46、42–46、44–48、44–48、44–48 |
| `NOISEBOT` | 2 | 18–23、19–24 |
| `OVICOPTER` | 2 | 124–130、126–132 |
| `OWL_MAGISTRATE` | 1 | 231 |
| `PARAFRIGHT` | 2 | 21、21 |
| `PHANTASMAL_GARDENER` | 1 | 26–31 |
| `PHROG_PARASITE` | 2 | 61–64、66–68 |
| `PUNCH_CONSTRUCT` | 1 | 55 |
| `QUEEN` | 1 | 400 |
| `ROCKET` | 2 | 199、209 |
| `SCROLL_OF_BITING` | 6 | 30–37、30–37、30–37、33–39、33–39、33–39 |
| `SHRINKER_BEETLE` | 2 | 38–40、40–42 |
| `SKULKING_COLONY` | 1 | 75 |
| `SLIMED_BERSERKER` | 2 | 261、281 |
| `SLUMBERING_BEETLE` | 2 | 86、89 |
| `SNAPPING_JAXFRUIT` | 1 | 34–36 |
| `SNEAKY_GREMLIN` | 1 | 10–14 |
| `SOUL_FYSH` | 1 | 211 |
| `SOUL_NEXUS` | 2 | 234、254 |
| `SPECTRAL_KNIGHT` | 2 | 93、97 |
| `SPINY_TOAD` | 2 | 116–119、121–124 |
| `STABBOT` | 2 | 18–23、19–24 |
| `TERROR_EEL` | 1 | 140 |
| `TEST_SUBJECT` | 3 | 100、111、111 |
| `THE_FORGOTTEN` | 2 | 106、111 |
| `THE_INSATIABLE` | 2 | 321、341 |
| `THE_LOST` | 2 | 93、99 |
| `THE_OBSCURA` | 2 | 123、129 |
| `THIEVING_HOPPER` | 1 | 79 |
| `TORCH_HEAD_AMALGAM` | 1 | 199 |
| `TOUGH_EGG` | 2 | 14–18、15–19 |
| `TUNNELER` | 2 | 87、92 |
| `TURRET_OPERATOR` | 1 | 41 |
| `TWIG_SLIME_M` | 1 | 26–28 |
| `TWIG_SLIME_S` | 1 | 7–11 |
| `TWO_TAILED_RAT` | 4 | 17–21、17–21、17–21、17–21 |
| `VANTOM` | 2 | 173、183 |
| `VINE_SHAMBLER` | 1 | 61 |
| `WATERFALL_GIANT` | 1 | 240 |
| `WRIGGLER` | 2 | 17–21、18–22 |
| `ZAPBOT` | 2 | 18–23、19–24 |

## 已知的根相依差異

目錄保存規範值；以下值依戰鬥當下狀態改變，匯入器必須自行套用。

- `SHIV`+0 的目標 @ sk-e-powers（場上有 `FAN_OF_KNIVES_POWER`，匯入時需改為全體敵人）
- `SHIV`+1 的目標 @ sk-e-powers（場上有 `FAN_OF_KNIVES_POWER`，匯入時需改為全體敵人）
