const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');

test('native pause resumes only effects that were playing', () => {
  const source = fs.readFileSync(path.join(__dirname, '../web.js'), 'utf8');
  const start = source.indexOf('const audio = {');
  const end = source.indexOf('// ---------- text fields', start);
  assert.ok(start >= 0 && end > start);
  const audio = vm.runInNewContext(source.slice(start, end) + '\naudio', {
    MUTED: false, running: true, document: { hidden: false }, handles: new Set(),
    assetName: name => name, soundUrls: new Map(),
  });
  const make = () => {
    const h = audio.create('effect.ogg');
    h.wantPlay = true;
    h.el = { paused: false, ended: false, plays: 0,
      pause() { this.paused = true; },
      play() { this.plays++; this.paused = false; return Promise.resolve(); },
    };
    return h;
  };
  const active = make();
  const ended = make();
  ended.el.paused = true;
  ended.el.ended = true;
  const stopped = make();
  stopped.el.paused = true;
  stopped.wantPlay = false;
  for (const h of [active, ended, stopped]) audio.pause(h);
  for (const h of [active, ended, stopped]) audio.resume(h);
  assert.equal(active.el.plays, 1);
  assert.equal(ended.el.plays, 0);
  assert.equal(stopped.el.plays, 0);
  audio.resume(active);
  assert.equal(active.el.plays, 1);
  audio.pause(active);
  audio.stop(active);
  audio.resume(active);
  assert.equal(active.el.plays, 1);
});

function audioModule() {
  const source = fs.readFileSync(path.join(__dirname, '../web.js'), 'utf8');
  const start = source.indexOf('const audio = {');
  const end = source.indexOf('// ---------- text fields', start);
  const document = { hidden: false };
  const audio = vm.runInNewContext(source.slice(start, end) + '\naudio', {
    MUTED: false, running: true, document, handles: new Set(), assetName: name => name, soundUrls: new Map(),
  });
  // A sound whose file is ready; refuse() makes the next play() fail like a browser blocking autoplay.
  const make = () => {
    const h = audio.create('track.ogg');
    let refuse = false;
    h.el = { paused: true, ended: false, loop: false, currentTime: 5, plays: 0,
      refuse() { refuse = true; },
      pause() { this.paused = true; },
      play() {
        if (refuse) { refuse = false; return Promise.reject(Object.assign(new Error('blocked'), { name: 'NotAllowedError' })); }
        this.plays++; this.paused = false; return Promise.resolve();
      },
    };
    return h;
  };
  return { audio, document, make };
}
const settle = () => new Promise(resolve => setImmediate(resolve));

test('music the game pauses while the tab is hidden stays paused when the tab returns', () => {
  const { audio, document, make } = audioModule();
  const bgm = make();
  audio.play(bgm, true);
  assert.equal(bgm.el.plays, 1);
  document.hidden = true; audio.pauseAll();
  audio.pause(bgm);                       // the game keeps running in the hidden tab
  document.hidden = false; audio.resumeAll();
  assert.equal(bgm.el.plays, 1);
  assert.equal(bgm.el.paused, true);
  audio.resume(bgm);                      // and its own resume still works
  assert.equal(bgm.el.plays, 2);
});

test('music the game resumes or starts while the tab is hidden plays when the tab returns', () => {
  const { audio, document, make } = audioModule();
  const resumed = make(), started = make(), effect = make();
  audio.play(resumed, true);
  audio.pause(resumed);
  document.hidden = true; audio.pauseAll();
  audio.resume(resumed);
  audio.play(started, true);
  audio.play(effect, false);
  assert.deepEqual([resumed.el.plays, started.el.plays, effect.el.plays], [1, 0, 0]);
  document.hidden = false; audio.resumeAll();
  assert.equal(resumed.el.plays, 2);
  assert.equal(started.el.plays, 1);
  assert.equal(started.el.loop, true);
  assert.equal(started.el.currentTime, 0);
  assert.equal(effect.el.plays, 0, 'a one-shot effect from the hidden tab is dropped');
});

test('music refused before the first tap plays on the next gesture; effects and stopped tracks do not', async () => {
  const { audio, make } = audioModule();
  const bgm = make(), effect = make(), stopped = make();
  for (const h of [bgm, effect, stopped]) h.el.refuse();
  audio.play(bgm, true);
  audio.play(effect, false);
  audio.play(stopped, true);
  await settle();
  assert.deepEqual([bgm.blocked, effect.blocked, stopped.blocked], [true, true, true]);
  audio.stop(stopped);
  audio.unlock();
  assert.deepEqual([bgm.el.plays, effect.el.plays, stopped.el.plays], [1, 0, 0]);
  audio.unlock();
  assert.equal(bgm.el.plays, 1, 'retried once');
});
