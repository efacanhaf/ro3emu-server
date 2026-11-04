# Role import: replicating a character from another server

`tools/RoleImport` writes a character exported from a live client into `data/ro3emu.db`. It exists so a
role can be replicated from the official server (or from any other realm) with EVERYTHING that decides
combat: level, job level, EXP, career, appearance, the six base attributes of the active plan, skills,
the skill bar, Astral Level, equipment with its revealed/rolled attributes and enchantments, equipment
tier (craft), refine/strengthen/luck per grid, cards socketed in the gear, Soul Echo ("eco de alma")
plans, talents, season page/refine, genre, growth class, paragon, title, career special, Valhalla statue,
totems, pets, mounts, wardrobe, social outfit, life jobs (+ restrict), library, bag state and currencies —
plus the quest journal and the finished guides, because the client's windows (Skills, Attributes,
Talents, auto-battle, ...) open by completed quests and not by a flag.

It is a tool, not gameplay: it writes the same rows and blobs the server itself would have written after
the same play, in one SQLite transaction, and it never changes server code or opens a connection while
the server is running.

## Pipeline

```
official/local client (probe injected)
  ro3/bot/ferramentas/exportar-personagem.ps1 -ClientPid <pid> -Saida <dir>
        │  one JSON per AUTO_SYNC_CLIENT_ROMSG_* dataset + snapshot.json
        ▼
  RoleImport --snapshot <dir>/snapshot.json --account <conta> --role-name <nome>
        │  role row, blobs, item rows, new item guids
        ▼
  ro3/bot/ferramentas/comparar-personagem.py --oficial <ref>/snapshot.json --local <new>/snapshot.json
           a table per system: equal / different (with the fields) / missing
```

The client side lives in `kaori-game-assist` (`ro3/bot/ferramentas/`): `exportar-personagem.ps1` reads
the sync bag only through `lua <path> <depth> <limit>` (read-only probe command), and `dump2json.py`
turns the probe text into JSON. Nothing in that path starts the agent, clicks, calls a game function or
reloads the client.

## Running it

The server must be stopped (it owns the database writer and the WAL); back the database up first.

```powershell
# 1. stop Ro3Emu.Server and copy the database (the -wal/-shm go with it)
Copy-Item data\ro3emu.db,data\ro3emu.db-wal,data\ro3emu.db-shm _scratch\backup-<hora>\

# 2. import (from the repository root)
dotnet run --project tools/RoleImport -c Release -- `
    --snapshot <pasta>\snapshot.json --account facanha --role-name Kaori

# 3. start the server again
kaori-game-assist\ro3\bot\ferramentas\servidor-local.ps1
```

Options:

| option | meaning |
|---|---|
| `--snapshot <path>` | `snapshot.json` written by `exportar-personagem.ps1` (required) |
| `--account <nome>` | account that owns the role; created when missing (a password is then required) |
| `--role-name <nome>` | role to create or replace (`role.name` is unique, case-insensitive) |
| `--db <path>` | database; defaults to `data/ro3emu.db` found above the executable |
| `--data-dir <dir>` | exported game data; defaults to the database's folder, then its ancestors |
| `--password-from-dpapi <conta>` | sets the account password from the saved auto-login credential |
| `--reset-password <conta>` | generates a random password, saves it with DPAPI, sets the account hash |
| `--dry-run` | builds everything, prints the summary, rolls the transaction back |

`--reset-password` is the path for an account nobody remembers: the new password is generated in memory,
written to `%LOCALAPPDATA%\kga-ro3\cred\<conta>.bin` (DPAPI, current user, entropy
`kga-ro3-autologin`, exactly what `ro3/autologin/salvar-credencial.ps1` writes) and never printed or
logged. `ro3/autologin/entrar.ps1 -Conta <conta>` then logs in with it.

`--dry-run` still needs the database (it reads the next free item guid and resolves the role), but every
write is rolled back.

## What is copied, and from where

Role row (`role` table):

| column | source |
|---|---|
| `level`, `job_level`, `exp`, `job_exp`, `career` | `ROLEBASEINFO` |
| `sex` | `ROLEBASEINFO.sex` |
| `hair` | `DBPERSONALINFO` current plan's `kAppearances[7]` |
| `stats` | `BASEATTRINFO.allPlanBaseProperty[curPlanIndex].baseProperty[1..6]`, the order STR/AGI/VIT/INT/DEX/LUK |
| `bonus_points` | see "stat points" below |
| `skills` | the LEARNED set: `DBSKILLDATA.skillAssignPlans[currentPlanIndex]`, each group at that plan's level (1 for a command skill the plan lacks) |
| `skill_bar` | that plan's `manualLoadSkill`, `slot:group`, empty slots omitted |
| `paragon_level`, `paragon_exp` | `DBPARAGONLEVEL.lv` / `.exp` |
| `currency` | `DBITEMSINFO.currency`, `id:amount` |
| `starter_kit` | always 1: an imported role must never receive the new-character kit |

Blobs (column → protobuf message in `src/Ro3Emu.Server/Protos/romsg`):

| column | message | sync dataset |
|---|---|---|
| `stat_plans` | `BaseAttrInfo` | `BASEATTRINFO` |
| `skill_plans` | `DBSkillData` | `DBSKILLDATA` |
| `season_talent` | `DBSeasonTalent` | `DBSEASONTALENT` |
| `season_page` | `DBSeasonPage` | `DBSEASONPAGE` |
| `season_refine` | `DBSeasonRefine` | `DBSEASONREFINE` |
| `equip_plans` | `DBEquipInfo` (plan fields only: `equipPlans`, `currentEquipPlan`, `craftInfo`, `CraftRankCount`, `cardInfo`, `inited`) | `DBEQUIPINFO` |
| `card_plans` | `DBStorageHoleInfo` | `DBSTORAGEHOLEINFO` |
| `genre` | `DBGenre` | `DBGENRE` |
| `growth_class` | `DBGrowthClassInfo` | `DBGROWTHCLASSINFO` |
| `paragon` | `DBParagonLevel` | `DBPARAGONLEVEL` |
| `title` | `DBTitleInfo` | `DBTITLEINFO` |
| `career_special` | `DBCareerSpecial` | `DBCAREERSPECIAL` |
| `totems` | `DBTotemInfo` | `DBTOTEMINFO` |
| `pets` | `DBPetInfo` | `DBPETINFO` |
| `wardrobe` | `DBWardRobeInfo` | `DBWARDROBEINFO` |
| `social_outfit` | `DBSocialOutfit` | `DBSOCIALOUTFIT` |
| `lifejob` | `DBLifeJobInfo` | `DBLIFEJOBINFO` |
| `life_restrict` | `DBRestrictInfo` | `DBRESTRICTINFO` |
| `library` | `DBLibraryInfo` | `DBLIBRARYINFO` |
| `tasks` | `DBTaskInfo` (the quest journal: delivered counters and running quests) | `DBTASKINFO` |
| `guides` | `DBGuideInfo` (finished tutorials) | `DBGUIDEINFO` |

JSON blobs (the server's own shapes, used through its own classes so the field names cannot drift):

| column | shape | source |
|---|---|---|
| `equip_levels` | `Game.EquipLevels {Strength, Refine, Luck}` per grid 1..7 | `DBEQUIPINFO.itemsStrength` / `itemsRefine` / `refineLuckyAdd` |
| `emblems` | `EmblemService.EmblemState` (Soul Echo plans, current plan, published power) | `DBEMBLEMINFO` + `ROLEBASEINFO.curPower`/`historyMaxPower` |
| `mounts` | `MountService.SavedState` carrying `DBMountsInfo` | `DBMOUNTSINFO` |
| `task_runtime` | `Game.TaskRuntimeState {CompletedGuides, InCutScene}` | rebuilt from `DBGUIDEINFO.finishedGuideIds` |
| `task_followup` | the server's pending-award debt (JSON string set) | always `[]`: the old local story's debt is dropped |

`task_runtime` carries the guides the quest objectives (`TGuide`) read, so it is built from the same
`DBGUIDEINFO` the client gets: no step of the previous local story survives, and no guide the official
client already finished fires again.

`heroic_spirit` is `DBOdinStatue`, which reaches the client inside `DBHEROICSPIRITINFO.odinStatue`
(that is the sync message feeding `role.heroic_spirit`; the column stores the statue alone).

`bag_state` is a `DBItemsInfo` WITHOUT the bags: the item rows are the `item` table, so what remains is
what the 5009 dataset carries that is not an item — item/share cooldowns, `useTimes`, quick-use list,
medicines auto-use, `currentArrowID` and the shortcut bar. `BuildItems` refills the packs and the
currencies on every sync.

Items (`item` table: `guid, role_id, entry, stack, pack, sort, grid, attrs, is_locked, enchantments,
str_attrs, bind_type, bind_expire`):

- every instance of `DBITEMSINFO.packInfo` packs 1, 2, 3, 4, 5 and 100 (`pack`, `sort` from
  `sortedIndex`, `grid` 0);
- every worn instance of `DBEQUIPINFO.items` (`pack` 2, `grid` from the ACTIVE plan's `itemsEquipGrid`);
- every card socketed in `DBITEMSINFO.storagePack` (`pack` 3, `grid` = `-1 * (equip grid * 10 + socket)`,
  which is `CharacterService.CardGrid`).

`attrs` is the instance's `itemInfos` as `{"key": longVal}` — the official encoding is the server's
(3 appraisal, 30+i / 60+i rolled base stats, 80+i specials, 100+i / 140+i entries, 1001-1100 Soul Echo;
see `Game/EquipmentAffixes.cs`). `strVal` entries (card cost history, 510-529) go to `str_attrs`, and
`enchantment` becomes `enchantments` (`{"row": {"Attr": {...}, "GetTime": ...}}`).

### Item guids

Official instance ids are ~3e17 and are replaced by new local ones. The plan allocates one guid per
instance in a stable order (bags by pack, then socketed cards, then worn gear), starting above the
database's highest item id, and then rewrites every reference through `GuidRemap`: any int64 field,
repeated element or map key/value the guid map knows. That covers the equipment plan grids, the Soul
Echo slots, the totem/pet selections and the shortcut bar without listing fields per message, so a new
guid field in a future client version is remapped too. `comparar-personagem.py` uses the same idea in
reverse (it matches instances by entry + rolls + enchantments + binding) to compare two snapshots.

### Stat points (`bonus_points`)

`CharacterService.TotalPoints` = `StartingStatPoints` (48) + `RoleProgress.BonusPoints` + the
`BaseLvConfig._iStatusPoint` of every level from 2 to the role's level. The official server's total is
`BaseAttrInfo.allAttrPoints` (464 for the reference character, which is exactly the level table without
the starting 48). The importer sets `bonus_points = official total - local base`, which is **-48** for a
character whose official total matches its level: `CharacterService` clamps the value at exactly
`-StartingStatPoints`, so the role ends with the official total and the official leftover points (8)
instead of 48 extra free points.

## What is NOT copied, and why

| family | why |
|---|---|
| achievements, guides' per-step records (`DBGuideInfo.guideFinishRecord`) | UI/account milestones; the finished-guide set IS copied (below) |
| daily/activities/lottery/shop/pass/month card | time-boxed account state, not a combat input |
| mail, auction, trade, stalls, market | other players' and the official economy's data; a replica starts with an empty mailbox and market |
| guild, friends, team, recent team | social identity of the official account; the local guild tables belong to the local realm |
| `settings` | the client's keybinds/UI options, tied to the official account and machine; the column is CLEARED on import (its values are binary and the old copy's "auto assign points" preference spent 2 of the 8 free stat points on every login — with the column NULL the client starts from its defaults and the points stay) |
| `personal` (profile card), `emoji_play`, `emoji_info`, `popularity`, `personal_upvote`, `adventure_group`, other adventure datasets, bar room, `clim_tower`, `wild_afk`, `dungeons`, `territory_*`, `area_concur`, `mvp`, `first_heart`, `training`, `timed_*`, `new_activities`, `activity_runtime` | per-realm event/instance progress; none of them changes the numbers the character fights with |
| `auto_decompose` | bag automation preference; the local default is kept |
| item instances the official client does not list | the export contains what the client holds; anything already gone (sold, consumed, mailed) cannot be recovered |

The import also never invents an item the account did not own: a reference to an instance the snapshot
does not carry (a shortcut-bar slot of a consumed item, a Soul Echo slot of a missing echo) is reported
in the tool's output and dropped rather than pointed at a dangling guid.

## Learned skills, the "Fila" tab and the skill bar

`role.skills` is the character's LEARNED set: the **active skill plan's assignments** (`currentPlanIndex`
is positional, so index 2 is the third plan). The client's skill window and the passives' stats both read
that plan, and the official evidence for it is direct: the naked reference character's holy resistance (317)
is 20, which is skill 11417 at level 1 — the level the third plan assigns while another plan has 6 and the
rest none; the union of the plans gave 120. `DBSkillData.skillList` is not the learned set (it lists every
skill that EXISTS for the character: 53 groups here, most with no points). Only the system/command groups
the client shows without points are added, at their `SkillConfig._iInitialLevel` (`_iSystemType` 6, the queue
command 11600 «Skill Bar»).

That was a real copy gap with a visible symptom: `lc_skillgrowthhelper:GetHasSkillQueue` shows the
"Fila" (skill queue) tab only when the client's skill list contains 11600, and the client's list is the
one the server sends. The import alone cannot make it appear, because `SkillService.Build` rebuilds the
list from the career tree; the tool therefore also lands the data the projection needs, and the server
sends it the way it already sends a card-granted skill:

- `SkillService.SyncAsync` adds every learned group the tree does not hold to the sent `skillList` and to
  the current plan's assignments (level from `role.skills`). Sent only — a plan switch still validates
  against the tree, exactly like the card-granted path above it;
- `SkillService.Build` puts the queue command on wheel slot 7 (`SkillWheelSkillQueue`) when the role
  learned it, which is where the original client has it.

Both are projections of data the role owns; no combat rule changes. Verified live on the reference
replica: the "Fila" tab appears, its queue shows Bloqueio Nv.5 / Escudo Reflexo Nv.1 / Canto Santo Nv.10
(the official screenshot), the skills the active plan does not assign read "Não aprendida" (Providência,
Escudo Luz, Escudada...), and `skillWheel.whellSkills[7].skillConfigId = 1160001` like the official.

**Wheel modes (the enhanced skill variants).** `SkillConfig` holds the same skill in several `_iMode`
rows: mode 0 is the base (1160710, damage 21000) and modes 1/5 are the enhanced variants the client puts
on a bar slot (1160720 damage 27000, 1160455 damage 8850 against 5500). The mode belongs to the slot
(`SkillWheelSlot.mode`), so it travels in the saved `skill_plans` blob — and a rebuild from the bar used
to reset every slot to mode 0, i.e. every login downgraded the rotation's damage. `SkillService.Build`
now keeps the saved slot's mode when the slot still holds the same group, and looks the row up with
`RealId(group, level, mode)`. Verified: the replica's wheel is now
`1=1160720(m1) 2=1160205(m0) 3=1162815(m1) 4=1160455(m5) 6=1161605(m0) 7=1160001(m0) 11=2334103`, the
official's list exactly.

## Naming a residual: the `!attrsrc` diagnostic

`CharacterService.AttributeProbe` (null in normal play) lets the GM command `!attrsrc [attrId]` watch the
same property pipeline the login publishes and log, per attribute, the final value, the pre-percentage
base and every source that fed it:

```
attrsrc 100001 101 (maxhp) final 113691 base 75005 raw 75005 <- extra:CombatService 2600, extra:PetService 1460,
   talentos 13000, conversao 560, equipe 21596, refino 9430, cartas 3398, nivel 5661, eco 17300
attrsrc 100001 102 (maxsp) final 1359 base 1345 raw 1345 <- conversao 5, nivel 1340
```

Read-only: it computes the sheet, prints it to the server log and replies a one-line summary. It is how
the reference replica's gap was measured source by source — and it shows the local pipeline is complete
against the exported tables (every source below matches its own config when recomputed): gear and rolled
affixes 21 596 HP, refine 9 430, echo 17 300, talents 13 000 (exactly the configuration's value for the
imported levels), cards 3 398, level row 5 661, passives 2 600, pet 1 460, conversions 560, enchantment
+2 STR; and for the percentages per_maxhp 5 139 (talents 3 740 + resonance 600 + conversion 280 + cards
264 + pet 255). Nothing is left to import: the residual is a rule of the official server that this
emulator does not implement (no table in the client's export carries it).

## Season talents: the two projections

`DBSEASONTALENT` stores two disjoint halves, and the client's map is their union:

- the **global** `talentNodes` map is the BOUGHT level of every ordinary node (`_iType` 1/2) and of the
  battle oath (4) — 167 nodes, 69 nonzero, sum 601 in the reference character;
- each of the five `plan`s holds only the profession choices (`_iType` 5 SkillPoint gate, 6 Profession_L,
  7 Profession_M) — 65 nodes per plan, at level 1 when that plan picked them; the reference character's
  plans 0/1 have 15 picked, the active plan (2) none;
- `unlockedTrees` (6), `unlockedNode` (134), `battleOathMaxLevel` (20) and `isActivated` are projections the
  server sends and the client shows as-is (`LC_DAL_SeasonTalent._processAllHandler` overlays the active plan
  on the global map; `UI_Sys_Skill_SeasonTalentPanelCtrl._refreshGrade` sums that map except the SkillPoint
  gates).

`SeasonTalentService` keeps that split: purchases go to the global map, choices to the active plan, `Seed`
moves anything an older save put in the other map, and the grade is the effective map's sum. Activation
walks the graph in tiers: an ordinary node (and a gate) opens with a maxed parent — the highest level of
that parent in ANY map counts, because the choice belongs to the character — a Profession_L opens with its
gate active and a Profession_M with its branch active; the oath opens with its cap. `ParentMaxed`-only
activation missed the 60 branch/option nodes of the opened gates, and a union-of-plans grade gave the wrong
total; both are gone.

`CharacterPlan.BuildSeasonTalent` now writes the official blob **untouched**. The login recompute
reproduces it exactly (measured on the reference replica, `DBSEASONTALENT` dumped from the client):

| point | official | local |
|---|---|---|
| unlocked trees | 6 (10101-10106) | 6 (10101-10106) |
| battle oath (level / cap) | 4 / 20 | 4 / 20 |
| global map (nodes / nonzero / sum) | 167 / 69 / 601 | 167 / 69 / 601 |
| per-node levels | — | identical |
| activated nodes | 134 | 134 (same set) |
| plan levels | — | identical |

The talent part of the sheet is therefore identical too: `!attrsrc` attributes 13 000 HP, 260 ATK/MATK,
130 DEF/MDEF, 300 res-crit, 150 ASPD/cast and 3 770 per_maxhp to the talents, and the naked sheet matches
the official on every attribute (see "Derived numbers" below).

## Quests, guides and the functions they open
Quest progress IS copied: `DBTASKINFO` goes into `role.tasks` and `DBGUIDEINFO` into `role.guides`. This
is what makes the character's windows work, because a function opens by a condition, not by a flag:

- `FunctionService.IsOpen` reads `FunctionConfig._kOpenCondition` -> `LC_ConditionList` sub-conditions
  (`1010`/`4004` quest delivered, `1002` base level, `1019` job rank, `1100` parent function open,
  `1014` tower floor, `1020` item count, `11001` life level, ...). `4004` is `TaskService.IsDelivered`
  (`completeTaskNum[id] > 0` or status 4), so the imported journal is what opens Skills, Attributes,
  Talents and the rest. Nothing is forced: the same table decides, and `FUNCTIONOPENINFO` is rebuilt
  from it on every sync.
- Constructor special cases stay intact: function `1003` (Season Talents) still needs the talent tables
  to exist, function `10` open and tasks `10101170` + `10103160` delivered — all of which the imported
  journal satisfies for a character that had them.
- `task_followup` is written empty: it is the previous local story's pending-award debt, and keeping it
  would block card/task settlement of the imported journal.
- `task_runtime.CompletedGuides` is rebuilt from the imported `DBGUIDEINFO`, so a finished tutorial does
  not fire again and the `TGuide` objectives count the same guides the official server counts.

On a replica of the reference character (279 functions open officially) every one of them is open
locally; the compare script lists any that is not, with its open condition
(`comparar-personagem.py --dados <data dir>`).

Two things follow from importing the journal, both by design:

- quests the official character had RUNNING are now running here, and a level/target already met makes
  the local server deliver them (and accept the next ones), so a replica can gain a few delivered
  tasks and their awards the official character had not claimed yet;
- quests the local `TaskConfig` no longer has are dropped at login (`TaskService.LoginAsync`), which is
  the same rule the client follows.

## After the import: the realm's own unlocks

The snapshot carries the character's DATA. A few systems are gated by the local realm's own
progression, so their effects stay inert until the local role unlocks them — the character is complete,
the feature is closed:

| system | gate (code) |
|---|---|
| Season talents | `SeasonTalentService.Available`: tasks `10101170` and `10103160` delivered (imported), functions `10` (Skills) and `1003` (Talent) open (derived) |
| Pets (deploy/incubate) | `PetService.Gate`: function `PetFunction` (and `3301`/`3302` for incubation) |
| Auto-battle, auto-potion, learn skills, auto-assign | `FunctionConfig._kOpenCondition` (level + tasks), see `docs/guide/open-content.md` |

With the journal imported these gates are met wherever the official character met them; the ones that
stay closed are listed by the compare script with their condition (on the reference replica only the
functions the local realm opens through `OpenContent` differ — the official ones are all open).

## Running it over a previous copy

The tool is meant to be re-run (Eduardo: "talvez tenhamos que fazer mais vezes"). Two things make a
second import behave like the first:

- the role's **economy receipts** are forgotten. A natural source key (`task/<role>/<task>/<count>/...`,
  `newactivity/<role>/...`) that committed once is refused the second time, and replacing the journal
  makes the server deliver the same awards again on the next login — without this the login dies with
  `EconomyOperationConflictException: Natural source already committed`. The import deletes the
  role-scoped `operation_source`/`operation_receipt` rows and their `economy_entry` legs, in the same
  transaction, so the ledger keeps matching the balances it writes;
- `settings` is written NULL (see the table above), so the client's own auto-assign preference from the
  previous copy cannot spend the imported free points.

## Verification

1. Unit tests: `dotnet test tests/Ro3Emu.Tests --filter "FullyQualifiedName~RoleImportTests"` — exact
   int64 parsing, guid allocation/remapping, the `attrs`/`str_attrs`/`enchantments` split, `EquipLevels`,
   the role columns, the quest journal + guides + rebuilt task runtime, and a full round trip through the
   server's own parsers using `tests/Ro3Emu.Tests/Fixtures/snapshot-kaori.json` (a real export,
   anonymised and trimmed).
2. Live: open a client against the local server (`ro3_multi`/autologin), inject the probe, run
   `exportar-personagem.ps1` on it, and compare with `comparar-personagem.py`. Every difference must be
   fixed or explained; the tool prints a note per derived value (for example `bonus_points`).
3. Functions: the compare script has a `FUNCTIONOPENINFO` section — every function open on the
   reference must be open here, and each one that is not is listed with its open condition
   (`--dados <data dir>` reads `FunctionConfig` + `LC_ConditionList`). On the reference replica: 279 of
   279 official functions open, plus 7 the local `OpenContent` opens by policy.
4. Derived numbers: the compare script also diffs `m_kHeadInfoData` (HP max, SP max, ATK, MATK, DEF,
   MDEF, ASPD, cast speed), dumped read-only with `lua <player>.m_kHeadInfoData 1 400`.

## Known limits

- Tasks the local `TaskConfig` no longer has are dropped at login, and a quest the official character had
  RUNNING continues here: the local server delivers it when its target is met and accepts the follow-ups,
  so a replica can end up with a few more delivered tasks (and their awards) than the official one.
- **Per-instance `playBook` and `season` are not persisted.** `RoleItemPlans.ItemValue` (and so the
  `item` table) has no column for them, and `CharacterService.ItemInfo` does not send them: a replica
  shows 0 where the official client shows 1. Nothing in the server reads them for stats (they tag the
  instance with the playbook/season that produced it), so the difference is cosmetic.
- The server recomputes what it derives: equipment rating (`itemInfos[2]`), plan powers, the item
  ordering (`sortedIndex`), the skill wheel/list and the global talent view. Those differ from the
  official dump by construction even when the inputs are identical.
- One instance occupies one equipment grid: the official client can list the same two-handed weapon in
  grids 1 and 2 of a plan, the server writes it in grid 1 and rebuilds the active plan on login.
- **Derived numbers: the naked reference character now matches.** Re-imported from a snapshot taken with the
  official character UNEQUIPPED (no gear, cards or Soul Echoes), the sheet is equal on every attribute —
  HP 31 491, SP 1 359, MATK 463, HIT/FLEE 1/1, RESP 135, per_maxhp 4 045, CRIT 21, res-crit 300, ASPD/ASPD
  base/cast 222/322/202, PVP dmg/be-dmg 705/475, per_treat/per_dmg/per_res_dmg 0, ele_reduce 0, holy resist
  20. Three rows stay exactly the official minus one buff: the snapshot was taken with `1162701` active
  (+10 ATK, +10 DEF, +10 MDEF, `_iTime` 0), a buff no exported table produces and no message carries, so the
  import cannot bring it. The commands that got there, all from official evidence rather than inference:
  pets read the star's base row (`PetStarConfig._iStage 0`, which is where `_kCollectAttr`/`_kFightSkill`
  live — reading the star-progress row silently dropped +380 HP, +19 ATK/MATK and +190 PVP each way),
  passives use the ACTIVE skill plan's levels (no union), and each conversion row contributes its whole part
  (`Math.Floor`: the official HIT 1 / FLEE 1 / RESP 135 against 2 / 2 / 136).

- The local client installation may be older than the official launcher's (`C:\Games\ROO_PC` vs
  `C:\Program Files\Genesis Launcher\ROO_PC`). A dataset the local client does not know travels as an
  unknown field and is ignored by the client, not by the server.
- The export is a snapshot in time: an account still being played on the official server keeps moving
  (level, EXP, drops). Compare against the snapshot, not against the live official client.
