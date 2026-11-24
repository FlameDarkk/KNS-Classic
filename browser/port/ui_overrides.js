// Browser-only UI overrides: game labels, sprites and buttons the browser build does not show.
// This file is the one place to edit. Rebuild with `python browser/port/build.py`.
// Nothing here deletes game code or art: remove an entry and the game draws (or answers taps on) it again.
//
// labels:  regexes matched against every string the game turns into text. A match draws nothing.
// sprites: one entry hides one game sprite.
//   tex:   '<w>x<h>/<hash>' of the GL texture, hashed from the pixels the game uploads (stable while the atlas is unchanged).
//          '*' hides every textured draw inside box (use for sprites whose texture changes, like counters).
//   box:   [x0, y0, x1, y1] in game pixels (2048x1184). A draw is hidden only if it lies fully inside. Omit for anywhere.
//   scene: g_ui16CurrentScene value (3 = title, 17 = lobby). Omit for any scene.
//   when:  optional extra condition; ui.lastLabel(regex) returns the latest drawn label that matches, or ''.
// touchAreas: native button rows to disable, so taps on them do nothing (no sound, no action).
//   table: the game's touch table symbol (lobby: touchPopChar). ids: row ids. scene: as above.
//
// Console helpers (browser, game running):
//   knsUi.pick()       then click a sprite: prints a ready-to-paste sprites entry and hides it now as a preview.
//   knsUi.undo()       shows the last picked sprite again.
//   knsUi.probe()      lists the textured draws of the next frames: { tex, box }.
//   knsUi.touchRows()  lists the native touch rows of the open screen: { id, rect: [top, bottom, left, right] }.
(function (root) {
'use strict';
const LOBBY = 17, TITLE = 3;
root.KnsUiOverrides = {
  labels: [
    /^\s*HIVE\s*$/, // lobby HIVE button label
    /File Check/, // title File Check label (signed out)
    /All Rights Reserved/, // title copyright line
  ],
  sprites: [
    { name: 'lobby HIVE plate', tex: '128x128/be30489c', scene: LOBBY, box: [930, 185, 1100, 355] },
    { name: 'lobby HIVE icon', tex: '64x32/db2b97bc', scene: LOBBY, box: [930, 185, 1100, 355] },
    { name: 'lobby gamepad icon', tex: '128x64/11aa0dab', scene: LOBBY, box: [350, 360, 505, 480] },
    { name: 'lobby Daily button (under Friend)', tex: '*', scene: LOBBY, box: [0, 350, 168, 500] },
    { name: 'lobby EXP button (under Messages)', tex: '*', scene: LOBBY, box: [168, 350, 340, 500] },
    // The File Check plate (signed out) and the Log Out plate (signed in) are one sprite; only the label tells them apart.
    { name: 'title File Check plate', tex: '256x64/9ac525e9', scene: TITLE, box: [0, 950, 300, 1110],
      when: ui => /File Check/.test(ui.lastLabel(/File Check|^\s*Log Out\s*$/)) },
  ],
  touchAreas: [
    // Lobby rows: 7 Friend, 8 Messages, 9 Story, 10 Settings, 15 unlabeled slot right of Settings (draws nothing),
    // 16 HIVE, 18 Daily, 19 EXP, 29 gamepad.
    { name: 'lobby HIVE, empty slot, Daily, EXP, gamepad', table: 'touchPopChar', scene: LOBBY, ids: [15, 16, 18, 19, 29] },
  ],
};
})(typeof globalThis !== 'undefined' ? globalThis : this);
