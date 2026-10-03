const { spawnSync } = require('node:child_process');
const { existsSync, readFileSync, mkdirSync, rmSync } = require('node:fs');
const path = require('node:path');
const root = path.resolve(__dirname, '..');
const coverage = path.resolve(root, 'tests', 'coverage');
const expected = require('../package.json').coverageAssembly + '.dll';
if (path.dirname(coverage) !== path.resolve(root, 'tests')) throw new Error('Invalid coverage output path.');
// Remove only this runner's generated coverage so a failed build cannot reuse old reports.
rmSync(coverage, { recursive: true, force: true });
mkdirSync(coverage, { recursive: true });
function run(args) {
  const result = spawnSync('dotnet', args, { cwd: root, stdio: 'inherit', shell: false });
  if (result.error) console.error(result.error.message);
  return result.status ?? 1;
}
let status = run(['tool', 'restore']);
if (status) process.exit(status);
status = run(['test', 'tests/unit/UnitTests.csproj', '--configuration', 'Release', '-p:CollectCoverage=true']);
const report = path.join(coverage, 'coverage.cobertura.xml');
if (!existsSync(report)) {
  console.error('Coverage report missing. Build/test failures are not successful coverage runs.');
  process.exit(status || 1);
}
const measured = Object.keys(JSON.parse(readFileSync(path.join(coverage, 'coverage.json'), 'utf8')));
if (measured.length !== 1 || measured[0] !== expected) {
  console.error('Coverage must measure only ' + expected + '; got: ' + measured.join(', '));
  status = status || 1;
}
const xml = readFileSync(report, 'utf8');
const coveredLines = Number(xml.match(/lines-valid="(\d+)"/)?.[1] ?? 0);
if (!coveredLines) {
  console.error('No application source was measured. Check the assembly filter.');
  status = status || 1;
}
const htmlStatus = run(['tool', 'run', 'reportgenerator', '-reports:' + report,
  '-targetdir:' + path.join(coverage, 'html'), '-reporttypes:Html;TextSummary']);
if (!htmlStatus) {
  const summary = path.join(coverage, 'html', 'Summary.txt');
  if (existsSync(summary)) console.log(readFileSync(summary, 'utf8'));
  console.log('HTML report: ' + path.join(coverage, 'html', 'index.html'));
}
process.exit(status || htmlStatus);
