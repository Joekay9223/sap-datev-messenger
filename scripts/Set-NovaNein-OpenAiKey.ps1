[CmdletBinding(SupportsShouldProcess)]
param(
  [string]$ServiceName = 'NovaNein-Staging',
  [string]$Model = 'gpt-5.6-terra',
  [SecureString]$ApiKey
)

$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
  $arguments = "-NoExit -NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -ServiceName `"$ServiceName`" -Model `"$Model`""
  Start-Process powershell.exe -Verb RunAs -ArgumentList $arguments
  return
}
if (-not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) { throw "Der Dienst $ServiceName wurde nicht gefunden." }
if (-not $ApiKey) {
  Add-Type -AssemblyName System.Windows.Forms
  Add-Type -AssemblyName System.Drawing
  $form = [Windows.Forms.Form]::new()
  $form.Text = 'NovaNein – OpenAI-Schlüssel'
  $form.StartPosition = 'CenterScreen'
  $form.FormBorderStyle = 'FixedDialog'
  $form.MaximizeBox = $false
  $form.MinimizeBox = $false
  $form.ClientSize = [Drawing.Size]::new(540, 140)
  $label = [Windows.Forms.Label]::new()
  $label.Text = 'OpenAI-API-Schlüssel hier vollständig einfügen:'
  $label.AutoSize = $true
  $label.Location = [Drawing.Point]::new(16, 16)
  $input = [Windows.Forms.TextBox]::new()
  $input.Location = [Drawing.Point]::new(16, 44)
  $input.Size = [Drawing.Size]::new(508, 24)
  $input.UseSystemPasswordChar = $true
  $input.ShortcutsEnabled = $true
  $ok = [Windows.Forms.Button]::new()
  $ok.Text = 'Speichern'
  $ok.Location = [Drawing.Point]::new(424, 92)
  $ok.DialogResult = [Windows.Forms.DialogResult]::OK
  $cancel = [Windows.Forms.Button]::new()
  $cancel.Text = 'Abbrechen'
  $cancel.Location = [Drawing.Point]::new(328, 92)
  $cancel.DialogResult = [Windows.Forms.DialogResult]::Cancel
  $form.AcceptButton = $ok
  $form.CancelButton = $cancel
  $form.Controls.AddRange(@($label, $input, $ok, $cancel))
  $form.Add_Shown({ $input.Focus() })
  if ($form.ShowDialog() -ne [Windows.Forms.DialogResult]::OK) { throw 'Eingabe wurde abgebrochen.' }
  $ApiKey = ConvertTo-SecureString $input.Text -AsPlainText -Force
  $input.Text = ''
  $form.Dispose()
}

function ConvertFrom-SecureStringPlain([SecureString]$Value) {
  $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Value)
  try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
  finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
}

$plain = ConvertFrom-SecureStringPlain $ApiKey
if ([string]::IsNullOrWhiteSpace($plain) -or -not $plain.StartsWith('sk-', [StringComparison]::Ordinal)) { throw 'Der eingegebene Wert sieht nicht wie ein OpenAI-API-Schlüssel aus.' }
$key = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
$existing = @((Get-ItemProperty -Path $key -Name Environment -ErrorAction SilentlyContinue).Environment)
$kept = $existing | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Where-Object { $name = $_.Split('=', 2)[0]; $name -notin 'OpenAI__ApiKey','OpenAI__Model' }
$updated = @($kept) + "OpenAI__ApiKey=$plain" + "OpenAI__Model=$Model"
if ($PSCmdlet.ShouldProcess($ServiceName, 'OpenAI-Schlüssel im geschützten Dienstkontext speichern und Dienst neu starten')) {
  & (Join-Path $PSScriptRoot 'Protect-NovaNein-ServiceRegistry.ps1') -ServiceName $ServiceName
  New-ItemProperty -Path $key -Name Environment -Value $updated -PropertyType MultiString -Force | Out-Null
  Restart-Service -Name $ServiceName -Force
  Write-Output 'OpenAI-Schlüssel wurde gespeichert und der NovaNein-Dienst wurde neu gestartet. Der Schlüssel wird nicht ausgegeben.'
}
