// Joins clips into one GIF with an ffmpeg two-pass palette, then checks it is under 15 s and 2 MB.
// Usage: node to-gif.mjs <plan.json>
// plan.json: { "out": "...gif", "width": 960, "fps": 10,
//              "clips": [ { "file": "a.webm", "from": 0, "to": 4.5, "speed": 1 }, ... ] }
// "from"/"to" are seconds into that file (omit for the whole file); "speed" > 1 shortens a wait.
// Relative clip files resolve from the raw-video folder (DEMO_RAW); a relative "out" from the repo root.
import { execFileSync } from 'node:child_process';
import { readFileSync, statSync } from 'node:fs';
import path from 'node:path';
import { RAW } from './lib.mjs';

const MAX_SECONDS = 15;
const MAX_BYTES = 2 * 1024 * 1024;

const plan = JSON.parse(readFileSync(process.argv[2], 'utf8'));
const width = plan.width ?? 960;
const fps = plan.fps ?? 10;

const inputs = plan.clips.flatMap(c => ['-i', path.resolve(RAW, c.file)]);
const parts = plan.clips.map((c, i) => {
  const trim = [c.from != null ? `start=${c.from}` : null, c.to != null ? `end=${c.to}` : null].filter(Boolean).join(':');
  return `[${i}:v]${trim ? `trim=${trim},` : ''}setpts=(PTS-STARTPTS)/${c.speed ?? 1},` +
    `scale=${width}:-2:flags=lanczos,fps=${fps},setsar=1[v${i}]`;
});
const joined = plan.clips.map((_, i) => `[v${i}]`).join('') + `concat=n=${plan.clips.length}:v=1:a=0[cat]`;
const palette = '[cat]split[a][b];[a]palettegen=max_colors=128:stats_mode=diff[p];' +
  '[b][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle[out]';

execFileSync('ffmpeg', ['-y', '-loglevel', 'error', '-threads', '2', ...inputs,
  '-filter_complex', [...parts, joined, palette].join(';'), '-map', '[out]', '-loop', '0', plan.out],
  { stdio: 'inherit' });

const seconds = Number(execFileSync('ffprobe', ['-v', 'error', '-show_entries', 'format=duration',
  '-of', 'csv=p=0', plan.out]).toString().trim());
const bytes = statSync(plan.out).size;
const ok = seconds < MAX_SECONDS && bytes < MAX_BYTES;
console.log(`${plan.out}: ${seconds.toFixed(1)} s, ${(bytes / 1048576).toFixed(2)} MB ${ok ? 'OK' : 'TOO BIG OR TOO LONG'}`);
process.exit(ok ? 0 : 1);
