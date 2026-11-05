# Boostix changelog

The English interface arrived in 3.2.0. Earlier versions are described in
[`CHANGELOG.md`](CHANGELOG.md), in Russian.

## 5.1.0 — 2026-11-06

### Fixes

- Boostix no longer takes resources from the game. In 5.0 the stage kept
  redrawing: the window behind a game used a fifth of a processor core, and
  the open stage almost half. Now the picture settles and stops, and the load
  is close to zero.
- The debrief no longer mixes up memory and video memory. If only video memory
  ran out, it says so.
- The page file tip appears only when the page file really is in the way: a
  fixed one at least the size of your memory now counts as fine.
- After a normal exit from a game the debrief no longer mentions a Windows
  event it could not find.
- The switch on the stage is now called Auto Boost, as in the hints.

### New

- Who took the memory: the debrief shows which programs held the most memory
  at the worst moment, and the tip says what to close before the next game.
- What's new tells you about everything you missed if you updated across
  several versions at once.

## 5.0.0 — 2026-11-05

### New

- The game stage: the whole window becomes the selected game. Its art fills
  the screen with its logo and a rail of covers, and the interface takes its
  colours from the art, so every game looks like itself.
- The live game: while Boost runs, the stage shows a timer, free memory, the
  processor, video memory and a chart that grows during the game.
- A full-screen debrief: the main conclusion, the numbers, the chart and a
  tip. After a long game it opens by itself.
- A journal instead of a table: every game is a strip of its own art, sorted
  by day, with the week's totals on top. 200 games are kept instead of 20.
- PC answers in one sentence whether the computer is ready, with four rings
  below it: memory, the Windows reserve, video memory and the page file.
- Your own picture for games without Steam art, such as Epic Games and GOG.
  A game's icon now shows on its cover.

### Fixes

- Starting with Windows no longer stops on a hidden update screen: Auto works
  at once and the update waits until you open the window.
- A Boostix that stays by the clock for days now learns about new versions.
- New games appear on the shelf by themselves, so the search button is gone.

## 4.0.0 — 2026-11-02

### New

- The main screen is now a shelf of your games with covers from Steam.
  Epic Games, GOG and games you added get a Boostix cover with the title.
- Play starts the game and turns on Auto for it, so Boost starts as soon as
  the game opens.
- A debrief after every game: free memory and processor load over the whole
  session, the moment memory was lowest, and three plain conclusions.
- New colours: deep navy, Gulf blue and racing orange. The Graphite and ice,
  Oxblood and gold, and the earlier Petrol and lemon themes are there too.
- A bigger window with sections along the top: Games, Now, PC, History and
  Settings. The PC check and background programs share one page.

### Fixes

- Test and demo launches no longer write sessions into your history.

## 3.2.1 — 2026-11-01

### Fixes

- Boost no longer fights a game over its priority. When a game sets its own
  priority, as CS2 and Dota 2 do, Boost leaves it alone until the session
  ends instead of raising it again every time you return to the game.
- The page file advice in the session report is in the interface language
  instead of English. So is the reason a game dropped out of the session.
- In the English interface every launch added a note to the crash log saying
  that no saved FPS measurement exists. That is not a crash, and the note is
  gone.

## 3.2.0 — 2026-10-31

### New

- English interface. Pick the language in Settings. By default Boostix
  follows the Windows display language and regional format: if either is
  Russian, the interface stays Russian.
- In-game signals: when the hotkey turns Boost on or off, a short sound plays
  and a badge appears over the game. After the game, a note by the clock says
  how long the session ran and whether memory was enough.
- Start with Windows: Boostix starts minimized next to the clock, and Boost
  turns on by itself for games with Auto.
- The Games section finds games installed with Steam, Epic Games and GOG. Add
  any of them to the list with one button.
- Four colour themes in Settings. A theme applies at once.
- The Boost gauge runs a self-test at launch, and while Boost runs the needle
  shows the processor load. The timer digits roll over.
- The icon by the clock shows whether Boost is on.
- The sidebar highlight slides to the chosen section.

### Fixes

- When the window loses focus, the glass fades to solid instead of switching
  abruptly.
- The installer text uses normal case instead of all capitals.
- With Windows animations turned off, Boostix does not animate either.

Details: [`release-notes-3.2.0.md`](release-notes-3.2.0.md).
