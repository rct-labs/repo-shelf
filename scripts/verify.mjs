// Acceptance oracle for the gate: runs the .NET and Node test suites and
// prints one summed "N passed" line. Exits non-zero if either suite fails.
// Usage: node scripts/verify.mjs [--dotnet-filter <expr>] [--node-only] [--dotnet-only]
import { spawnSync } from 'node:child_process';

const args = process.argv.slice(2);
const flag = (name) => args.includes(name);
const value = (name) => {
  const i = args.indexOf(name);
  return i >= 0 ? args[i + 1] : undefined;
};

let passed = 0;
let failed = false;

function run(cmd, cmdArgs, env) {
  const r = spawnSync(cmd, cmdArgs, {
    encoding: 'utf8',
    shell: process.platform === 'win32',
    env: { ...process.env, ...env },
    maxBuffer: 64 * 1024 * 1024,
  });
  const out = (r.stdout || '') + (r.stderr || '');
  process.stdout.write(out.split('\n').slice(-40).join('\n') + '\n');
  if (r.status !== 0) failed = true;
  return out;
}

if (!flag('--node-only')) {
  const filter = value('--dotnet-filter');
  const dotnetArgs = ['test', 'app/RepoShelf.slnx', '--nologo'];
  if (filter) dotnetArgs.push('--filter', `"${filter}"`);
  const out = run('dotnet', dotnetArgs, { DOTNET_CLI_UI_LANGUAGE: 'en' });
  const m = [...out.matchAll(/Passed:\s+(\d+)/g)];
  if (m.length === 0) failed = true;
  for (const x of m) passed += Number(x[1]);
}

if (!flag('--dotnet-only')) {
  const out = run('node', ['--test', '"tests/**/*.test.js"']);
  const m = out.match(/ℹ pass (\d+)/);
  if (!m) failed = true;
  else passed += Number(m[1]);
}

console.log(`${passed} passed`);
process.exit(failed ? 1 : 0);
