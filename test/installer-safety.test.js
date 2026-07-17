import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { existsSync, mkdtempSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { dirname, join, parse } from 'node:path';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';
import test from 'node:test';

const readScript = (name) => readFileSync(new URL(`../scripts/${name}`, import.meta.url), 'utf8');
const repositoryRoot = dirname(dirname(fileURLToPath(import.meta.url)));
const powershellExecutable = [
  process.env.ProgramFiles && join(process.env.ProgramFiles, 'PowerShell', '7', 'pwsh.exe'),
  process.env.ProgramW6432 && join(process.env.ProgramW6432, 'PowerShell', '7', 'pwsh.exe'),
].find((candidate) => candidate && existsSync(candidate)) || 'powershell.exe';
const uninstaller = join(repositoryRoot, 'scripts', 'Uninstall-NovaNein-Client.ps1');
const sapVersionCheck = join(repositoryRoot, 'scripts', 'Test-NovaNein-SapClassicVersion.ps1');
const clientInstaller = join(repositoryRoot, 'scripts', 'Install-NovaNein-Client.ps1');
const validateUninstallPath = (installDirectory) => spawnSync(
  powershellExecutable,
  ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', uninstaller, '-InstallDirectory', installDirectory, '-ValidateOnly'],
  { encoding: 'utf8', windowsHide: true },
);

test('Client-Deinstallation löscht niemals den gemeinsamen ProgramData-Stamm rekursiv', () => {
  const script = readScript('Uninstall-NovaNein-Client.ps1');
  assert.doesNotMatch(script, /Remove-Item\s+-LiteralPath\s+\$settingsDirectory\s+-Recurse/i);
  assert.match(script, /Remove-Item\s+-LiteralPath\s+\$settings\s+-Force/i);
});

test('Client-Deinstallation akzeptiert nur den kanonischen Clientbereich unter Program Files', () => {
  const programFiles = process.env.ProgramFiles;
  assert.ok(programFiles, 'ProgramFiles fehlt in der Windows-Testumgebung.');
  const clientRoot = join(programFiles, 'NovaNein', 'Client');

  assert.equal(validateUninstallPath(clientRoot).status, 0);
  assert.equal(validateUninstallPath(join(clientRoot, 'Pilot')).status, 0);
  assert.notEqual(validateUninstallPath(`${clientRoot}Fremd`).status, 0);
  assert.notEqual(validateUninstallPath(join(clientRoot, '..', 'Server')).status, 0);
  assert.notEqual(validateUninstallPath(parse(programFiles).root).status, 0);
});

test('Client-Deinstallation verweigert ProgramData und gemeinsame NovaNein-Serverpfade', () => {
  const programData = process.env.ProgramData;
  assert.ok(programData, 'ProgramData fehlt in der Windows-Testumgebung.');

  assert.notEqual(validateUninstallPath(join(programData, 'NovaNein')).status, 0);
  assert.notEqual(validateUninstallPath(join(programData, 'NovaNein', 'Server')).status, 0);
  assert.notEqual(validateUninstallPath(join(programData, 'NovaNein', 'SapAddonInstaller')).status, 0);
});

test('Client-Deinstallation entfernt zuerst die Nutzlast überprüfbar und erst danach Zertifikat und Konfiguration', () => {
  const script = readScript('Uninstall-NovaNein-Client.ps1');
  assert.match(script, /NovaNein-Companion-AtLogOn/);
  const payloadMove = script.indexOf('Move-Item -LiteralPath $InstallDirectory -Destination $quarantine -ErrorAction Stop');
  const payloadDelete = script.indexOf('Remove-Item -LiteralPath $quarantine -Recurse -Force -ErrorAction Stop');
  const certificateDelete = script.indexOf("Get-ChildItem 'Cert:\\LocalMachine\\My'");
  const settingsDelete = script.indexOf('Remove-Item -LiteralPath $settings -Force -ErrorAction Stop');
  assert.ok(payloadMove >= 0 && payloadDelete > payloadMove, 'Die Nutzlast wird nicht atomar aus dem aktiven Pfad entfernt.');
  assert.ok(certificateDelete > payloadDelete, 'Das Zertifikat wird vor der überprüfbaren Payload-Löschung entfernt.');
  assert.ok(settingsDelete > certificateDelete, 'Die Konfiguration wird zu früh entfernt.');
  assert.match(script, /NovaNein\.SapAddonHost\.exe/);
  assert.doesNotMatch(script, /Remove-Item\s+-LiteralPath\s+\$InstallDirectory\s+-Recurse\s+-Force\s+-ErrorAction\s+SilentlyContinue/i);
});

test('Serverdaten besitzen bei Neuinstallationen einen eigenen Unterordner', () => {
  const script = readScript('install-novanein-service.ps1');
  assert.match(script, /DataRoot\s*=\s*'C:\\ProgramData\\NovaNein\\Server'/i);
});

test('Dienst-Secrets werden erst nach Einschränkung der Registry-ACL geschrieben', () => {
  for (const name of ['Set-NovaNein-ServiceSecrets.ps1', 'Set-NovaNein-OpenAiKey.ps1']) {
    const script = readScript(name);
    const protect = script.indexOf('Protect-NovaNein-ServiceRegistry.ps1');
    const write = script.indexOf('New-ItemProperty -Path $key -Name Environment');
    assert.ok(protect >= 0, `${name} ruft die Registry-Härtung nicht auf.`);
    assert.ok(write > protect, `${name} schreibt Secrets vor der Registry-Härtung.`);
  }
  const protector = readScript('Protect-NovaNein-ServiceRegistry.ps1');
  assert.match(protector, /SetAccessRuleProtection\(\$true, \$false\)/);
  assert.match(protector, /S-1-5-18/);
  assert.match(protector, /S-1-5-32-544/);
  assert.match(protector, /S-1-5-32-545/);
});

test('SAP-Installer ersetzt bestehende Dateien mit Sicherung und Rollback', () => {
  const source = readFileSync(new URL('../src/NovaNein.SapAddonInstaller/Program.cs', import.meta.url), 'utf8');
  assert.match(source, /File\.Replace\(staged, target, backup/);
  assert.match(source, /RollBack\(changes\)/);
  assert.match(source, /StateDirectory => Path\.Combine\(LegacyStateDirectory, "SapAddonInstaller"\)/);
});

test('SAP-Paketbau berücksichtigt SAPs punktlosen Versionsvergleich', () => {
  const check = (version, previous) => spawnSync(
    powershellExecutable,
    ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', sapVersionCheck, '-Version', version, '-PreviousRegisteredVersion', previous],
    { encoding: 'utf8', windowsHide: true },
  );

  assert.equal(check('1.1.0.0', '1.0.0.7').status, 0);
  assert.equal(check('1.0.0.8', '1.0.0.7').status, 0);
  assert.notEqual(check('1.1.0', '1.0.0.7').status, 0);
  assert.notEqual(check('1.0.0.7', '1.0.0.7').status, 0);
  assert.notEqual(check('1.0.0.6', '1.0.0.7').status, 0);
});

test('SAP-Classic-Paket definiert den automatischen Add-on-Start', () => {
  const script = readScript('New-NovaNein-SapClassicPackage.ps1');
  assert.match(script, /addonname=\"NovaNein\" addongroup=\"A\"/);
  assert.doesNotMatch(script, /addonname=\"NovaNein\" addongroup=\"M\"/);
});

test('SAP-Lightweight-Paket behält den kanonischen Add-on-Namen trotz versioniertem ZIP-Namen', () => {
  const script = readScript('New-NovaNein-SapLightweightPackage.ps1');
  assert.match(script, /\[string\]\$AddonName\s*=\s*'NovaNein'/);
  assert.match(script, /\$packageOutput\s*=\s*Join-Path\s+\$stage\s+"\$AddonName\.zip"/);
  assert.match(script, /Copy-Item\s+-LiteralPath\s+\$packageOutput\s+-Destination\s+\$output/);
  assert.match(script, /ExtName\s+-ne\s+\$AddonName/);
});

test('Clientinstaller prüft Nutzlastmanifest und erkennt beschädigte Pakete', () => {
  const root = mkdtempSync(join(tmpdir(), 'novanein-client-manifest-'));
  try {
    const payload = join(root, 'payload');
    mkdirSync(payload);
    const content = 'NovaNein-Client-Testpayload';
    writeFileSync(join(payload, 'payload.txt'), content, 'utf8');
    writeFileSync(join(root, 'payload-manifest.json'), JSON.stringify({
      SchemaVersion: 1,
      ProductVersion: '1.1.0',
      WorkstationName: 'test-01',
      Files: [{
        Path: 'payload.txt',
        Length: Buffer.byteLength(content),
        Sha256: createHash('sha256').update(content).digest('hex'),
      }],
    }), 'utf8');

    const validate = () => spawnSync(
      powershellExecutable,
      ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', clientInstaller, '-PackageRoot', root, '-ServerUrl', 'https://192.0.2.10:5189/', '-ValidateOnly'],
      { encoding: 'utf8', windowsHide: true },
    );
    const validResult = validate();
    assert.equal(validResult.status, 0, [validResult.error?.message, validResult.stdout, validResult.stderr].filter(Boolean).join('\n'));
    writeFileSync(join(payload, 'payload.txt'), 'manipuliert', 'utf8');
    const tamperedResult = validate();
    assert.notEqual(tamperedResult.status, 0, 'Die manipulierte Nutzlast wurde unerwartet akzeptiert.');
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('Clientinstaller behandelt finally-Blöcke als Sprachkonstrukt statt als Laufzeitbefehl', () => {
  const command = [
    '$tokens=$null;$errors=$null;',
    `$ast=[System.Management.Automation.Language.Parser]::ParseFile('${clientInstaller.replaceAll("'", "''")}',[ref]$tokens,[ref]$errors);`,
    'if($errors.Count){exit 2};',
    "$bad=@($ast.FindAll({param($n) $n -is [System.Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'finally'},$true));",
    'if($bad.Count){exit 3}',
  ].join(' ');
  const result = spawnSync(powershellExecutable, ['-NoProfile', '-Command', command], { encoding: 'utf8', windowsHide: true });
  assert.equal(result.status, 0, result.stderr || result.stdout);
});

test('Clientpaket enthält einen fehlertoleranten Ein-Klick-Launcher und Installationshinweise', () => {
  const script = readScript('Build-NovaNein-ClientPackage.ps1');
  assert.match(script, /Install-NovaNein-Client\.cmd/);
  assert.match(script, /Start-NovaNein-Companion\.cmd/);
  assert.match(script, /--companion/);
  assert.match(script, /if errorlevel 1/);
  assert.match(script, /README-Installation\.txt/);
  assert.match(script, /authentifizierte[nr]? Health-Test HTTP 200/);
});

test('Clientinstaller registriert den SAP-Begleiter nur auf SAP-Arbeitsplätzen', () => {
  const script = readScript('Install-NovaNein-Client.ps1');
  assert.match(script, /SAP\\SAP Business One\\SAP Business One\.exe/);
  assert.match(script, /Register-ScheduledTask/);
  assert.match(script, /NovaNein-Companion-AtLogOn/);
  assert.match(script, /-Argument '--companion'/);
});

test('SAP-Registrierungsreparatur ist standardmäßig nur Diagnose und erzeugt ein Backup', () => {
  const script = readScript('Repair-NovaNein-SapLocalRegistration.ps1');
  assert.match(script, /\[switch\]\$Apply/);
  assert.match(script, /\.backup-/);
  assert.match(script, /X64Exe = 'NovaNein\.SapAddonHost\.exe'/);
  assert.match(script, /\[string\]\$InstallerName/);
  assert.match(script, /\[string\]\$InstallerMD5/);
  assert.match(script, /InstallPkgMD5/);
  assert.match(script, /if \(-not \$Apply\) \{ return \}/);
});

test('Client-Rollout ist nicht künstlich auf sieben Windows-Arbeitsplätze begrenzt', () => {
  const script = readScript('New-NovaNein-ClientRollout.ps1');
  assert.doesNotMatch(script, /WorkstationName\.Count\s*-gt\s*7/i);
});

test('SAP-SQL-Dienstkonto erhält nur Leserechte und ausdrückliche Schreib-DENYs', () => {
  const script = readScript('Grant-NovaNein-SapReadOnlySql.ps1');
  assert.match(script, /ALTER ROLE \[db_datareader\] ADD MEMBER/);
  assert.match(script, /REVOKE CONTROL TO/);
  assert.match(script, /DENY INSERT, UPDATE, DELETE, EXECUTE, ALTER TO/);
  assert.doesNotMatch(script, /db_owner|db_datawriter/i);
});
