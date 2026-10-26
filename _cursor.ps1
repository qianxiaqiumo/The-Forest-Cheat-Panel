$ErrorActionPreference = 'Continue'
$cecil = 'F:\steam\steamapps\common\The Forest\BepInEx\core\Mono.Cecil.dll'
[void][Reflection.Assembly]::LoadFrom($cecil)
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly('F:\steam\steamapps\common\The Forest\TheForest_Data\Managed\Assembly-CSharp.dll')

Write-Output '=== 引用 Cursor.lockState / Screen.lockCursor 的方法 ==='
foreach ($t in $asm.MainModule.Types) {
    foreach ($m in $t.Methods) {
        if (-not $m.HasBody) { continue }
        foreach ($i in $m.Body.Instructions) {
            $op = [string]$i.Operand
            if ($op -match 'Cursor::set_lockState|Cursor::set_visible|Screen::set_lockCursor') {
                Write-Output ('  ' + $t.FullName + '.' + $m.Name + '  ->  ' + $op)
                break
            }
        }
    }
}
