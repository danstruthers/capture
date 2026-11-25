<#
.SYNOPSIS
    End-to-end smoke test of the capture client and the viewer page.

.DESCRIPTION
    Starts capture.exe and the viewer host, opens the viewer page in headless Microsoft Edge and
    drives it through its controls over the DevTools protocol: watching every screen over H.264,
    choosing which screens to watch, zooming, popping a screen out into its own window, starting,
    pausing, resuming and stopping a recording, changing the frame rate, switching to JPEG pictures
    and back, choosing which audio devices are recorded, pausing, stopping and starting the stream,
    and switching between separate screens and the whole desktop.

    Every check is made on numbers and states the pages report (frames shown, codec, recording
    state, ...). The test never looks at the picture itself.

    Needs a built solution (dotnet build), an interactive desktop session and Microsoft Edge.
    Exits with the number of failed checks.

.EXAMPLE
    dotnet build; ./tests/EndToEnd.ps1
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Debug',
    [int]$CapturePort = 8091,
    [int]$ViewerPort = 5184,
    [int]$DebugPort = 9343,

    # Keep the recordings and logs (in a folder under the temp directory) instead of deleting them.
    [switch]$KeepOutput
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$captureExe = Join-Path $root "src\Capture\bin\$Configuration\net8.0-windows\capture.exe"
$viewerExe = Join-Path $root "src\Capture.Viewer\bin\$Configuration\net8.0\Capture.Viewer.exe"
$edgeExe = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1

foreach ($needed in @($captureExe, $viewerExe)) {
    if (-not (Test-Path $needed)) { throw "$needed is missing. Build the solution first (dotnet build -c $Configuration)." }
}
if (-not $edgeExe) { throw 'Microsoft Edge was not found.' }

$work = Join-Path ([IO.Path]::GetTempPath()) "capture-e2e-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
New-Item -ItemType Directory -Force $work | Out-Null
$captureLog = Join-Path $work 'capture.log'

$script:failures = 0
$script:commandId = 0
$script:pages = @{}       # name -> DevTools socket of that page
$script:current = $null   # the socket Get-Js talks to

# Waits for a process to listen on a port. Generous: on a busy machine the capture client can take
# a good while to start. Gives up at once if the process has exited.
function Wait-Port([int]$port, [System.Diagnostics.Process]$process, [int]$timeoutSeconds = 60) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if ($process -and $process.HasExited) { throw "$($process.ProcessName) exited with code $($process.ExitCode) before listening on port $port." }
        $client = [System.Net.Sockets.TcpClient]::new()
        try { $client.Connect('127.0.0.1', $port); return } catch { Start-Sleep -Milliseconds 200 } finally { $client.Dispose() }
    }
    throw "Nothing started listening on port $port within $timeoutSeconds s."
}

# Attaches to the page whose address matches, under a name; it becomes the current page.
function Connect-Page([string]$name, [string]$urlPattern, [int]$timeoutSeconds = 15) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    $page = $null
    while (-not $page -and (Get-Date) -lt $deadline) {
        try {
            $page = @(Invoke-RestMethod "http://127.0.0.1:$DebugPort/json") | ForEach-Object { $_ } |
                Where-Object { $_.type -eq 'page' -and $_.url -like $urlPattern } | Select-Object -First 1
        }
        catch {
            # A busy Edge can drop a request to its DevTools endpoint; ask again.
        }
        if (-not $page) { Start-Sleep -Milliseconds 200 }
    }
    if (-not $page) { return $false }

    $socket = [System.Net.WebSockets.ClientWebSocket]::new()
    $socket.ConnectAsync([Uri]$page.webSocketDebuggerUrl, [Threading.CancellationToken]::None).Wait()
    $script:pages[$name] = $socket
    $script:current = $socket

    # The page can be found before its module script has run; every check reads captureViewer.
    while ((Get-Js 'typeof captureViewer') -ne 'object' -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 100 }
    return (Get-Js 'typeof captureViewer') -eq 'object'
}

function Use-Page([string]$name) { $script:current = $script:pages[$name] }

function Invoke-DevTools([string]$method, [hashtable]$parameters = @{}) {
    $script:commandId++
    $id = $script:commandId
    $json = @{ id = $id; method = $method; params = $parameters } | ConvertTo-Json -Depth 8 -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes($json)
    $script:current.SendAsync([ArraySegment[byte]]::new($bytes), 'Text', $true, [Threading.CancellationToken]::None).Wait()

    # Edge answers in milliseconds; a minute without an answer means it has hung, and the run would too.
    $buffer = [byte[]]::new(1MB)
    $timeout = [Threading.CancellationTokenSource]::new([TimeSpan]::FromMinutes(1))
    try {
        while ($true) {
            $stream = [IO.MemoryStream]::new()
            do {
                $result = $script:current.ReceiveAsync([ArraySegment[byte]]::new($buffer), $timeout.Token).GetAwaiter().GetResult()
                $stream.Write($buffer, 0, $result.Count)
            } while (-not $result.EndOfMessage)
            $message = [Text.Encoding]::UTF8.GetString($stream.ToArray()) | ConvertFrom-Json -Depth 32
            if ($message.id -eq $id) { return $message.result }
        }
    }
    catch [OperationCanceledException] {
        throw "Edge did not answer $method within a minute."
    }
    finally {
        $timeout.Dispose()
    }
}

# Evaluates JavaScript in the current page and returns its value.
function Get-Js([string]$expression) {
    $result = Invoke-DevTools 'Runtime.evaluate' @{ expression = $expression; returnByValue = $true; awaitPromise = $true; userGesture = $true }
    if ($result.exceptionDetails) { throw "JavaScript failed: $($result.exceptionDetails.exception.description)" }
    return $result.result.value
}

function Write-Result([bool]$passed, [string]$what, [string]$detail = '') {
    if ($passed) {
        Write-Host "  ok    $what"
    }
    else {
        $script:failures++
        Write-Host "  FAIL  $what$(if ($detail) { " - $detail" })" -ForegroundColor Red
    }
}

# Passes as soon as the JavaScript condition is true; fails if it is not within the timeout. An
# exception (say, a stream that has not appeared yet) counts as not true yet; the last one is reported.
function Assert-Eventually([string]$what, [string]$condition, [int]$timeoutSeconds = 15) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    $lastError = $null
    do {
        try {
            if ((Get-Js "Boolean($condition)") -eq $true) { Write-Result $true $what; return }
        }
        catch {
            if ($_.Exception.Message -notlike 'JavaScript failed*') { throw }
            $lastError = $_.Exception.Message
        }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)
    Write-Result $false $what "still false after $timeoutSeconds s: $condition$(if ($lastError) { " ($lastError)" })"
}

function Write-Skipped([string]$what) { Write-Host "  (skipped: $what)" }

function Assert-That([string]$what, [bool]$condition, [string]$detail = '') { Write-Result $condition $what $detail }

function Invoke-Click([string]$selector) { Get-Js "document.querySelector(`"$selector`").click()" | Out-Null }

function Set-Field([string]$id, [string]$value) {
    Get-Js "(() => { const field = document.getElementById('$id'); field.value = '$value'; field.dispatchEvent(new Event('change')); })()" | Out-Null
}

# Chooses an entry of a select element by its position, for values that are awkward to quote (camera ids).
function Select-Option([string]$id, [int]$index) {
    Get-Js "(() => { const field = document.getElementById('$id'); field.selectedIndex = $index; field.dispatchEvent(new Event('change')); })()" | Out-Null
}

# Ticks or unticks an audio device in the page's Audio devices list.
function Switch-AudioDevice([string]$id) {
    Get-Js "[...document.querySelectorAll('#audio-devices input')].find(box => box.dataset.device === '$id').click()" | Out-Null
}

$processes = @()
try {
    # An empty configuration file, so that a local capture.json cannot change what is tested.
    $config = Join-Path $work 'capture.json'
    Set-Content -Path $config -Value '{}'

    Write-Host 'Options that name a screen, camera or audio device that is not there'
    foreach ($option in @(@('--screens', '64'), @('--audio-device', 'NoDeviceIsCalledThis'), @('--camera', 'NoCameraIsCalledThis'))) {
        $errors = Join-Path $work 'bad-option.err'
        $run = Start-Process -FilePath $captureExe -WorkingDirectory $work -PassThru -Wait -WindowStyle Hidden `
            -ArgumentList (@('--config', $config) + $option) -RedirectStandardOutput (Join-Path $work 'bad-option.log') -RedirectStandardError $errors
        $message = (Get-Content $errors -Raw) -replace '\s+', ' '
        Assert-That "$($option -join ' ') is refused as a mistake in the options" ($run.ExitCode -eq 2 -and $message -match '--list-devices') "exit code $($run.ExitCode): $message"
    }

    Write-Host "Starting the capture client, the viewer host and Edge (output in $work)"
    # The duration is a safety net: capture.exe ends by itself if this script dies without stopping it.
    $capture = Start-Process -FilePath $captureExe -WorkingDirectory $work -PassThru -WindowStyle Hidden `
        -ArgumentList @('--config', $config, '--port', $CapturePort, '--layout', 'separate', '--no-record', '--output-dir', '.', '--stats', '0', '--duration', '600') `
        -RedirectStandardOutput $captureLog -RedirectStandardError (Join-Path $work 'capture.err')
    $viewer = Start-Process -FilePath $viewerExe -WorkingDirectory (Join-Path $root 'src\Capture.Viewer') -PassThru -WindowStyle Hidden `
        -ArgumentList @('--urls', "http://localhost:$ViewerPort") -RedirectStandardOutput (Join-Path $work 'viewer.log')
    $processes += $capture, $viewer
    Wait-Port $CapturePort $capture
    Wait-Port $ViewerPort $viewer

    $pageUrl = "http://localhost:$ViewerPort/?autoconnect&url=ws://localhost:$CapturePort/"
    $edge = Start-Process -FilePath $edgeExe -PassThru -ArgumentList @(
        '--headless=new', "--remote-debugging-port=$DebugPort", "--user-data-dir=`"$(Join-Path $work 'edge-profile')`"",
        '--autoplay-policy=no-user-gesture-required', '--no-first-run', '--window-size=1500,1000', $pageUrl)
    $processes += $edge
    Wait-Port $DebugPort $null
    if (-not (Connect-Page 'main' "http://localhost:$ViewerPort/?autoconnect*")) { throw 'The viewer page did not open in Edge.' }

    $stats = 'captureViewer.stats'
    $state = 'captureViewer.state'

    Write-Host 'H.264 over WebRTC, one stream per screen'
    Assert-Eventually 'the page receives the capture state' "$state !== null && $state.sources.length > 0"
    $sources = @(Get-Js "$state.sources.map(s => s.id)")
    Add-Type -AssemblyName System.Windows.Forms
    $screenCount = [System.Windows.Forms.Screen]::AllScreens.Count
    Assert-That 'there is one stream per screen' ((Get-Js "$state.layout") -eq 'separate' -and $sources.Count -eq $screenCount) "layout $(Get-Js "$state.layout"), $screenCount screens, streams $($sources -join ', ')"
    Assert-Eventually 'every screen has a tile' "${stats}?.tiles?.length === $($sources.Count)"
    foreach ($id in $sources) {
        Assert-Eventually "$id is playing" "${stats}?.sources?.['$id']?.framesDecoded > 20" 30
    }
    Assert-That 'the video codec is H.264' ((Get-Js "$stats.video.codec") -like 'H.264*')
    $hasAudio = (Get-Js "$state.audio.enabled") -eq $true
    if ($hasAudio) {
        Assert-Eventually 'audio packets arrive' "${stats}?.audio?.packetsReceived > 50"
        Assert-That 'the audio codec is G.711' ((Get-Js "$stats.audio.codec") -like 'G.711*')
    }
    else {
        Write-Skipped 'the audio checks: the capture client has no audio'
    }

    $first = $sources[0]
    if ($sources.Count -ge 2) {
        $second = $sources[1]
        Write-Host 'Choosing screens'
        Invoke-Click "#screens input[data-source='$second']"
        Assert-Eventually "$second is no longer shown" "!$stats.tiles.includes('$second')" 5
        Assert-Eventually "the capture client stops capturing $second" "$state.sources.find(s => s.id === '$second').capturing === false && $state.sources.find(s => s.id === '$second').viewers === 0" 10
        Assert-That "$first keeps playing" ((Get-Js "$state.sources.find(s => s.id === '$first').viewers") -eq 1)
        Invoke-Click "#screens input[data-source='$second']"
        Assert-Eventually "$second is shown and playing again" "$stats.tiles.includes('$second') && $stats.sources['$second']?.framesPerSecond > 5" 15
    }
    else {
        Write-Skipped 'choosing screens needs two screens'
    }

    Write-Host 'Zoom'
    $zoomIn = "(() => { const view = document.querySelector('.tile[data-source=`"$first`"] .tile-view'); const r = view.getBoundingClientRect(); " +
              "view.dispatchEvent(new WheelEvent('wheel', { deltaY: -600, clientX: r.left + r.width * 0.25, clientY: r.top + r.height * 0.25, bubbles: true, cancelable: true })); " +
              "return document.querySelector('.tile[data-source=`"$first`"] .tile-zoom').textContent; })()"
    $zoomed = Get-Js $zoomIn
    Assert-That 'the mouse wheel zooms in' ([int]($zoomed -replace '%', '') -gt 100) "zoom shows $zoomed"
    Assert-That 'the picture is scaled' ((Get-Js "getComputedStyle(document.querySelector('.tile[data-source=`"$first`"] video')).transform") -ne 'none')
    Get-Js "document.querySelector('.tile[data-source=`"$first`"] .tile-view').dispatchEvent(new MouseEvent('dblclick', { bubbles: true }))" | Out-Null
    Assert-That 'double-click resets the zoom' ((Get-Js "document.querySelector('.tile[data-source=`"$first`"] .tile-zoom').textContent") -eq '100%')

    Write-Host 'Pop-out window'
    Invoke-Click ".tile[data-source='$first'] .tile-popout"
    Assert-Eventually "$first leaves the main window" "$stats.popouts.includes('$first') && !$stats.tiles.includes('$first')" 5
    if (Connect-Page 'popout' "*popout=$first*") {
        Assert-Eventually "the pop-out window plays $first" "${stats}?.sources?.['$first']?.framesDecoded > 20 && $stats.tiles.length === 1" 30
        Assert-That 'the pop-out window has no sound of its own' ($null -eq (Get-Js "$stats.audio"))
        Use-Page 'main'
        Assert-Eventually 'the capture client sends the pop-out its screen' "$state.streaming.clients === 2 && $state.sources.find(s => s.id === '$first').viewers === 1"
        Use-Page 'popout'
        Invoke-Click '.tile-popout' # "Close" in a pop-out window
        Use-Page 'main'
        Assert-Eventually "closing the pop-out brings $first back" "!$stats.popouts.includes('$first') && $stats.tiles.includes('$first')" 10
        Assert-Eventually "$first plays in the main window again" "$stats.sources['$first']?.framesPerSecond > 5" 15
    }
    else {
        Write-Result $false 'the pop-out window opens' 'no page with popout= appeared'
    }

    Write-Host 'Recording control'
    Invoke-Click '#recording-start'
    Assert-Eventually 'recording starts with one file per stream' "$state.recording.state === 'recording' && $state.recording.files.length === $($sources.Count) && $state.recording.seconds > 1"
    Invoke-Click '#recording-pause'
    Assert-Eventually 'recording pauses' "$state.recording.state === 'paused'"
    Start-Sleep -Milliseconds 1200
    $pausedAt = [double](Get-Js "$state.recording.seconds")
    Start-Sleep -Seconds 2
    $stillAt = [double](Get-Js "$state.recording.seconds")
    Assert-That 'nothing is recorded while paused' ($stillAt -eq $pausedAt) "$pausedAt s -> $stillAt s"
    Invoke-Click '#recording-pause'
    Assert-Eventually 'recording resumes' "$state.recording.state === 'recording' && $state.recording.seconds > $($pausedAt + 1)"
    Invoke-Click '#recording-stop'
    Assert-Eventually 'recording stops' "$state.recording.state === 'stopped'"
    $deadline = (Get-Date).AddSeconds(30) # finalizing takes a second or two, far longer on a busy machine
    # Case-sensitive: "Recordings are saved in ..." at startup must not count.
    while ((Get-Date) -lt $deadline -and @(Select-String -Path $captureLog -CaseSensitive -Pattern 'Saved .*\.mp4').Count -lt $sources.Count) { Start-Sleep -Milliseconds 250 }
    $recordings = @(Get-ChildItem $work -Filter *.mp4)
    Assert-That 'an MP4 file was saved for every stream' ($recordings.Count -eq $sources.Count -and -not ($recordings | Where-Object Length -lt 20KB)) "found: $(($recordings | ForEach-Object { "$($_.Name) $($_.Length) bytes" }) -join ', ')"

    Write-Host 'Frame rate'
    Set-Field 'frame-rate' '10'
    Assert-Eventually 'the capture client reports 10 fps' "$state.video.frameRate === 10"
    # Each stream gets a new encoder for the new rate; once they are running the rate settles at about 10.
    Assert-Eventually 'the page receives about 10 fps' "${stats}?.video?.framesPerSecond >= 8 && $stats.video.framesPerSecond <= 12" 30

    Write-Host 'Refused requests'
    Set-Field 'layout' 'grid' # no such option: the select sends '', which the capture client refuses
    Assert-Eventually 'the refusal is shown under Remote control' "document.getElementById('control-note').dataset.kind === 'error' && document.getElementById('control-note').textContent.includes('layout')"
    Assert-That 'and not under Audio devices' ((Get-Js "document.getElementById('audio-note').dataset.kind") -ne 'error')
    Set-Field 'layout' 'separate'
    Assert-Eventually 'the next request clears it' "document.getElementById('control-note').dataset.kind !== 'error'"

    Write-Host 'JPEG pictures over the WebSocket'
    Set-Field 'video-mode' 'jpeg'
    foreach ($id in $sources) {
        Assert-Eventually "pictures of $id are shown" "${stats}?.sources?.['$id']?.codec === 'JPEG' && $stats.sources['$id'].framesDecoded > 10" 20
    }
    if ($hasAudio) {
        Assert-Eventually 'PCM audio arrives' "${stats}?.audio?.codec?.startsWith('PCM') && $stats.audio.packetsReceived > 50"

        Write-Host 'Audio devices'
        Assert-Eventually 'the page lists every audio device with a tick box' "$state.audio.devices.length >= 2 && document.querySelectorAll('#audio-devices input[type=checkbox]').length === $state.audio.devices.length"
        Assert-That 'each group starts with the Windows default' ((Get-Js "['input', 'output'].every(kind => $state.audio.devices.find(d => d.kind === kind).id === 'default-' + kind)") -eq $true)
        $selectedAtStart = Get-Js "JSON.stringify($state.audio.selected)"
        $output = Get-Js "$state.audio.devices.find(d => d.kind === 'output' && d.present && !d.id.startsWith('default-'))?.id ?? null"
        if ($output -and $selectedAtStart -eq '["default-input"]') {
            Switch-AudioDevice $output
            Assert-Eventually 'a playback device is recorded as well' "$state.audio.selected.includes('$output') && $state.audio.devices.find(d => d.id === '$output').status === 'capturing'" 20
            Switch-AudioDevice 'default-input'
            Assert-Eventually 'the default recording device is no longer recorded' "!$state.audio.selected.includes('default-input') && $state.audio.selected.length === 1"
            $before = [int](Get-Js "$stats.audio.packetsReceived")
            Start-Sleep -Seconds 2
            Assert-That 'audio keeps arriving from the playback device' ([int](Get-Js "$stats.audio.packetsReceived") -gt $before + 50)
            Switch-AudioDevice $output
            Assert-Eventually 'with nothing ticked the page says the audio is silence' "$state.audio.selected.length === 0 && document.getElementById('audio-note').textContent.includes('silence')"
            Switch-AudioDevice 'default-input'
            Assert-Eventually 'the default recording device is recorded again' "$state.audio.source === 'microphone' && ['capturing', 'starting'].includes($state.audio.devices[0].status)"
        }
        else {
            Write-Skipped "ticking devices needs a playback device and the default audio source; selected: $selectedAtStart"
        }
    }

    Write-Host 'Streaming control'
    Invoke-Click '#streaming-pause'
    Assert-Eventually 'streaming pauses' "$state.streaming.state === 'paused'"
    Start-Sleep -Milliseconds 1200
    $shownAt = [int](Get-Js "$stats.video?.framesDecoded ?? 0")
    Start-Sleep -Seconds 2
    $shownLater = [int](Get-Js "$stats.video?.framesDecoded ?? 0")
    Assert-That 'no pictures arrive while paused' ($shownLater -eq $shownAt) "$shownAt -> $shownLater"
    Invoke-Click '#streaming-pause'
    Assert-Eventually 'streaming resumes' "$state.streaming.state === 'streaming' && $stats.video?.framesDecoded > $shownAt"
    Invoke-Click '#streaming-stop'
    Assert-Eventually 'streaming stops' "$state.streaming.state === 'stopped' && $stats.video === null && $stats.tiles.length === 0"
    Invoke-Click '#streaming-start'
    Assert-Eventually 'streaming starts again' "$state.streaming.state === 'streaming' && $stats.video?.framesDecoded > 5"

    Write-Host 'Back to H.264'
    Set-Field 'video-mode' 'h264'
    Set-Field 'frame-rate' '30'
    Assert-Eventually 'H.264 video is playing again' "${stats}?.video?.codec?.startsWith('H.264') && $stats.video.framesDecoded > 20 * $stats.video.streams" 30

    Write-Host 'The whole desktop as one stream'
    $duplicating = (Get-Js "$state.sources.every(s => s.capture === 'DXGI desktop duplication')") -eq $true
    Set-Field 'layout' 'desktop'
    Assert-Eventually 'the capture client switches to one desktop stream' "$state.layout === 'desktop' && $state.sources.length === 1 && $state.sources[0].id === 'desktop'" 20
    Assert-Eventually 'the desktop is playing' "${stats}?.tiles?.length === 1 && $stats.sources?.desktop?.framesDecoded > 20" 30
    $desktop = Get-Js "JSON.stringify({ stream: [$state.sources[0].videoWidth, $state.sources[0].videoHeight], shown: [$stats.sources.desktop.width, $stats.sources.desktop.height] })"
    $sizes = $desktop | ConvertFrom-Json
    Assert-That 'the desktop picture has the size the capture client encodes' (($sizes.stream -join 'x') -eq ($sizes.shown -join 'x')) $desktop
    Set-Field 'layout' 'separate'
    Assert-Eventually 'the capture client switches back to one stream per screen' "$state.layout === 'separate' && $state.sources.length === $($sources.Count)" 20
    Assert-Eventually 'every screen plays again' "${stats}?.tiles?.length === $($sources.Count) && $stats.tiles.every(id => $stats.sources[id]?.framesDecoded > 20)" 30
    if ($duplicating) {
        # The new streams take the screens over from the old ones rather than falling back to GDI.
        $fallbacks = @(Select-String -Path $captureLog -Pattern 'capturing it with GDI')
        Assert-That 'layout changes keep desktop duplication' ($fallbacks.Count -eq 0 -and (Get-Js "$state.sources.every(s => s.capture === 'DXGI desktop duplication')") -eq $true) (($fallbacks | ForEach-Object Line) -join '; ')
    }

    Write-Host 'The camera'
    $cameras = [int](Get-Js "$state.camera.devices.length")
    if ($cameras -gt 0) {
        Assert-That 'the camera is off at first, and every camera can be chosen' ((Get-Js "$state.camera.selected === null && document.getElementById('camera').options.length === $cameras + 1") -eq $true)
        Set-Field 'camera-rate' '15'
        Assert-Eventually 'the camera has a frame rate of its own' "$state.camera.frameRate === 15 && $state.video.frameRate === 30"
        Select-Option 'camera' 1
        Assert-Eventually 'the camera becomes a stream of its own' "$state.camera.selected === $state.camera.devices[0].id && $state.sources.some(s => s.id === 'camera' && s.kind === 'camera')" 20
        Assert-Eventually 'the camera has a tile and plays' "${stats}?.tiles?.includes('camera') && $stats.sources?.camera?.framesDecoded > 20" 40
        Assert-That "the camera's video is H.264" ((Get-Js "$stats.sources.camera.codec") -like 'H.264*')
        Assert-Eventually 'the camera arrives at about 15 fps' "$stats.sources.camera.framesPerSecond >= 11 && $stats.sources.camera.framesPerSecond <= 18" 30
        Assert-That 'the screens keep their own frame rate' ((Get-Js "$state.sources.filter(s => s.kind !== 'camera').every(s => s.frameRate === 30) && $state.sources.find(s => s.kind === 'camera').frameRate === 15") -eq $true)
        Assert-That 'the Camera section says how the camera runs' ((Get-Js "document.getElementById('camera-note').dataset.kind !== 'error' && document.getElementById('camera-note').textContent.includes('H.264')") -eq $true) (Get-Js "document.getElementById('camera-note').textContent")

        Invoke-Click '#recording-start'
        Assert-Eventually 'a recording includes the camera' "$state.recording.state === 'recording' && $state.recording.files.some(f => f.endsWith('-camera.mp4'))" 20
        Start-Sleep -Seconds 3
        Invoke-Click '#recording-stop'
        Assert-Eventually 'recording stops' "$state.recording.state === 'stopped'"
        $deadline = (Get-Date).AddSeconds(30)
        while ((Get-Date) -lt $deadline -and -not (Select-String -Path $captureLog -CaseSensitive -Pattern 'Saved .*-camera\.mp4')) { Start-Sleep -Milliseconds 250 }
        $cameraFile = Get-ChildItem $work -Filter '*-camera.mp4' | Select-Object -First 1
        Assert-That 'an MP4 file was saved for the camera' ($cameraFile -and $cameraFile.Length -gt 20KB) "$($cameraFile.Name) $($cameraFile.Length) bytes"

        Set-Field 'video-mode' 'jpeg'
        Assert-Eventually 'the camera is shown as JPEG pictures too' "${stats}?.sources?.camera?.codec === 'JPEG' && $stats.sources.camera.framesDecoded > 10" 30
        Set-Field 'video-mode' 'h264'

        Select-Option 'camera' 0
        Assert-Eventually 'switching the camera off removes its stream and its tile' "$state.camera.selected === null && !$state.sources.some(s => s.kind === 'camera') && !${stats}?.tiles?.includes('camera')" 20
        Assert-Eventually 'the screens play on' "${stats}?.video?.codec?.startsWith('H.264') && $stats.tiles.length === $($sources.Count) && $stats.tiles.every(id => $stats.sources[id]?.framesDecoded > 20)" 30
        Set-Field 'camera-rate' '30'
    }
    else {
        Write-Skipped 'the camera checks: this machine has no camera'
    }

    Assert-That 'the page shows no control error' ((Get-Js "document.getElementById('control-note').dataset.kind") -ne 'error') (Get-Js "document.getElementById('control-note').textContent")

    foreach ($socket in $script:pages.Values) { $socket.Dispose() }
}
catch {
    # Startup failures and JavaScript exceptions end the run; they count, and get the logs shown below.
    Write-Result $false 'the run completed' $_.Exception.Message
}
finally {
    foreach ($process in $processes) {
        if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
    }
    # Edge starts helper processes of its own; they all carry the profile folder on their command line.
    Get-CimInstance Win32_Process -Filter "Name = 'msedge.exe'" | Where-Object { $_.CommandLine -like "*$work*" } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

    if ($script:failures -gt 0) {
        foreach ($log in @($captureLog, (Join-Path $work 'capture.err'), (Join-Path $work 'viewer.log'))) {
            if ((Test-Path $log) -and (Get-Item $log).Length -gt 0) {
                Write-Host "--- $(Split-Path -Leaf $log) ---"
                Get-Content $log | Select-Object -Last 40 | Write-Host
            }
        }
    }

    if ($KeepOutput -or $script:failures -gt 0) {
        Write-Host "Output kept in $work"
    }
    else {
        Start-Sleep -Milliseconds 500
        Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host $(if ($script:failures -eq 0) { 'All checks passed.' } else { "$script:failures check(s) failed." })
exit $script:failures
