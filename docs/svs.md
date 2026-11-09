# SvS (Warzone Duel) - bracket and forecast

## Bracket (Henry 2026-10)

8 servers, groups A (a0-a3) and B (b0-b3).
- Week 1: a0 v a1, a2 v a3, b0 v b1, b2 v b3.
- Week 2 stays INSIDE each group: winner(a0a1) v winner(a2a3), loser(a0a1) v loser(a2a3); same in B.
  (#1967's example on hosted: 1967 then meets 1980.)
- Week 3 crosses: A winner v B winner = final, then 3rd/4th etc.
Code: `web/src/panels/Svs.jsx` (r2 = play winners/losers within each group, ROUTE labels per group, BracketView).
Hand-entered setups: table svs_setup, SvS tab "+ New SvS".

## Model v1.5 (2026-10-09) - `src/LastWar.Storage/SvsModel.cs`

- Henry: logged scores are for LEARNING the model, never to correct a forecast ("it's very hard to get those scores").
  The v1.4 anchor was removed and its forecasts deleted.
- Side(Top1Hero, Top100Hero, ActiveHero, TopHeroes[5 strongest active], Alliances).
- Duel match-ups per day = mean of the two servers' alliances with 10+ members active in 3 days; split 50/50.
- Trucks 1.35K per billion active hero per day, share by active hero^1.3.
- Arms Race 3.24K per billion per day, share by active hero^0.6.
- Boss warzone damage goes to the bigger top-100 hero power.
- Boss 1st place is a chance (FirstPlaceChance): 5 strongest active per side, damage = hero x exp(N(0, 0.12)),
  4,000 draws, seed 87. (A hard "bigger top-1" call was wrong for #1981 v #1970.)
- Inputs from SvsStore strength SQL (topHeroes ';'-joined, InvariantCulture). Tests: SvsModelTests.cs.
- "Active" here uses player_activity, so the idle_seen change (2026-11-03) lowers active counts mostly below HQ25;
  HQ30+ moved only ~4%.
- Open: who actually won boss 1st on #1981 v #1970 (asked Henry, no answer yet).
