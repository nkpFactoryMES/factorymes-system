# 로컬 Modbus 테스트만 수행한다. 기존 원장·계측·DB 볼륨은 삭제하지 않는다.
# Fault는 로컬 Modbus simulator만 잠시 정지/재기동하며 finally에서 복구한다.
param(
  [ValidateSet('Protocol', 'Collection', 'Fault', 'Browser')]
  [string]$Mode = 'Collection'
)

$ErrorActionPreference = 'Stop'
$factoryRoot = Split-Path -Parent $PSScriptRoot

function Confirm-Exit([string]$step) {
  if ($LASTEXITCODE -ne 0) {
    throw "$step failed"
  }
}

Push-Location -LiteralPath $factoryRoot
try {
  switch ($Mode) {
    'Protocol' {
      docker compose -f deploy/compose.yaml run --rm tooling verify-modbus-protocol
      Confirm-Exit 'Strict Modbus wire and decoding verification'
    }
    'Collection' {
      docker compose `
        -f deploy/compose.yaml `
        -f deploy/compose.collection-verification.yaml `
        -f deploy/compose.analysis-verification.yaml `
        run --rm tooling verify-modbus-collection
      Confirm-Exit 'Actual Modbus collection, AUTO and reporting'
    }
    'Browser' {
      Push-Location -LiteralPath tests/browser
      try {
        node verify-modbus-collection.cjs
        Confirm-Exit 'Modbus administrator browser verification'
      }
      finally {
        Pop-Location
      }
    }
    'Fault' {
      docker compose -f deploy/compose.yaml run --rm tooling verify-modbus-protocol
      Confirm-Exit 'Linux strict Modbus protocol checks'
      Set-Content verification-results/modbus-stage.json '{"stage":"starting"}'
      Set-Content verification-results/modbus-ack.txt '' -NoNewline
      $modbusOut = "$PWD/verification-results/modbus-fault-run.log"
      $modbusErr = "$PWD/verification-results/modbus-fault-error.log"
      $modbusRun = Start-Process -FilePath docker -ArgumentList @(
        'compose',
        '-f',
        'deploy/compose.yaml',
        '-f',
        'deploy/compose.collection-verification.yaml',
        '-f',
        'deploy/compose.analysis-verification.yaml',
        'run',
        '--rm',
        '--name',
        'factorymes-modbus-verification',
        '-e',
        'MES_MODBUS_FAULT_VERIFICATION=1',
        'tooling',
        'verify-modbus-collection'
      ) -WindowStyle Hidden -PassThru -RedirectStandardOutput $modbusOut -RedirectStandardError $modbusErr
      $modbusHandled = ''
      $modbusUntil = (Get-Date).AddMinutes(5)
      try {
        while (-not $modbusRun.HasExited -and (Get-Date) -lt $modbusUntil) {
          $modbusPhase = $null
          try {
            $modbusPhase = (Get-Content verification-results/modbus-stage.json -Raw |
              ConvertFrom-Json).stage
          }
          catch {
            # 제어 파일 작성 중의 짧은 빈 값은 다음 확인에서 다시 읽는다.
          }
          if ($modbusPhase -and $modbusPhase -ne 'starting' -and $modbusPhase -ne $modbusHandled) {
            switch ($modbusPhase) {
              'stop-simulator' {
              docker compose -f deploy/compose.yaml stop modbus-simulator
            }
              'restart-simulator' {
              docker compose -f deploy/compose.yaml up -d --no-deps --no-build --wait modbus-simulator
            }
              default {
              throw 'Unknown Modbus verification phase'
            }
            }
            Confirm-Exit "Modbus controller $modbusPhase"
            Set-Content verification-results/modbus-ack.txt $modbusPhase -NoNewline
            $modbusHandled = $modbusPhase
          }
          Start-Sleep -Milliseconds 150
          $modbusRun.Refresh()
        }
        if (-not $modbusRun.HasExited) {
          throw 'Modbus test timeout'
        }
        $modbusRun.WaitForExit()
        Get-Content -LiteralPath $modbusOut
        Get-Content -LiteralPath $modbusErr
        if ($modbusRun.ExitCode -ne 0) {
          throw 'Modbus fault test failed'
        }
      }
      finally {
        if (-not $modbusRun.HasExited) {
          docker stop factorymes-modbus-verification
        }
        docker compose -f deploy/compose.yaml up -d --no-deps --no-build --wait modbus-simulator
        Confirm-Exit 'Restore Modbus simulator'
      }
    }
  }
}
finally {
  Pop-Location
}
