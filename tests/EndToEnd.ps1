<#
.SYNOPSIS
    End-to-end smoke test of the capture client and the viewer page.

.DESCRIPTION
    Starts capture.exe and the viewer host, opens the viewer page in headless Microsoft Edge and
    drives it through its controls over the DevTools protocol: watching every screen over H.264,
    choosing which screens to watch, zooming, popping a screen out into its own window, starting,
    pausing, resuming and stopping a recording, changing the frame rate, switching to JPEG pictures
    and back, switching the audio source, pausing, stopping and starting the stream, and switching
    between separate screens and the whole desktop.

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

function Wait-Port([int]$port, [int]$timeoutSeconds = 20) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $client = [System.Net.Sockets.TcpClient]::new()
        try { $client.Connect('127.0.0.1', $port); return } catch { Start-Sleep -Milliseconds 200 } finally { $client.Dispose() }
    }
    throw "Nothing started listening on port $port."
}

# Attaches to the page whose address matches, under a name; it becomes the current page.
function Connect-Page([string]$name, [string]$urlPattern, [int]$timeoutSeconds = 15) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    $page = $null
    while (-not $page -and (Get-Date) -lt $deadline) {
        $page = @(Invoke-RestMethod "http://127.0.0.1:$DebugPort/json") | ForEach-Object { $_ } |
            Where-Object { $_.type -eq 'page' -and $_.url -like $urlPattern } | Select-Object -First 1
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

    $buffer = [byte[]]::new(1MB)
    while ($true) {
        $stream = [IO.MemoryStream]::new()
        do {
            $result = $script:current.ReceiveAsync([ArraySegment[byte]]::new($buffer), [Threading.CancellationToken]::None).Result
            $stream.Write($buffer, 0, $result.Count)
        } while (-not $result.EndOfMessage)
        $message = [Text.Encoding]::UTF8.GetString($stream.ToArray()) | ConvertFrom-Json -Depth 32
        if ($message.id -eq $id) { return $message.result }
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

# Passes as soon as the JavaScript condition is true; fails if it is not within the timeout.
function Assert-Eventually([string]$what, [string]$condition, [int]$timeoutSeconds = 15) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    do {
        if ((Get-Js "Boolean($condition)") -eq $true) { Write-Result $true $what; return }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)
    Write-Result $false $what "still false after $timeoutSeconds s: $condition"
}

function Assert-That([string]$what, [bool]$condition, [string]$detail = '') { Write-Result $condition $what $detail }

function Invoke-Click([string]$selector) { Get-Js "document.querySelector(`"$selector`").click()" | Out-Null }

function Set-Field([string]$id, [string]$value) {
    Get-Js "(() => { const field = document.getElementById('$id'); field.value = '$value'; field.dispatchEvent(new Event('change')); })()" | Out-Null
}

$processes = @()
try {
    Write-Host "Starting the capture client, the viewer host and Edge (output in $work)"
    $capture = Start-Process -FilePath $captureExe -WorkingDirectory $work -PassThru -WindowStyle Hidden `
        -ArgumentList @('--port', $CapturePort, '--layout', 'separate', '--no-record', '--output-dir', '.', '--stats', '0', '--duration', '400') `
        -RedirectStandardOutput $captureLog -RedirectStandardError (Join-Path $work 'capture.err')
    $viewer = Start-Process -FilePath $viewerExe -WorkingDirectory (Join-Path $root 'src\Capture.Viewer') -PassThru -WindowStyle Hidden `
        -ArgumentList @('--urls', "http://localhost:$ViewerPort") -RedirectStandardOutput (Join-Path $work 'viewer.log')
    $processes += $capture, $viewer
    Wait-Port $CapturePort
    Wait-Port $ViewerPort

    $pageUrl = "http://localhost:$ViewerPort/?autoconnect&url=ws://localhost:$CapturePort/"
    $edge = Start-Process -FilePath $edgeExe -PassThru -ArgumentList @(
        '--headless=new', "--remote-debugging-port=$DebugPort", "--user-data-dir=`"$(Join-Path $work 'edge-profile')`"",
        '--autoplay-policy=no-user-gesture-required', '--no-first-run', '--window-size=1500,1000', $pageUrl)
    $processes += $edge
    Wait-Port $DebugPort
    if (-not (Connect-Page 'main' "http://localhost:$ViewerPort/?autoconnect*")) { throw 'The viewer page did not open in Edge.' }

    $stats = 'captureViewer.stats'
    $state = 'captureViewer.state'

    Write-Host 'H.264 over WebRTC, one stream per screen'
    Assert-Eventually 'the page receives the capture state' "$state !== null && $state.sources.length > 0"
    $sources = @(Get-Js "$state.sources.map(s => s.id)")
    Assert-That 'there is one stream per screen' ((Get-Js "$state.layout") -eq 'separate' -and $sources.Count -ge 1) "layout $(Get-Js "$state.layout"), streams $($sources -join ', ')"
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
        Assert-That 'the pop-out window has no sound of its own' ((Get-Js "$stats.audio") -eq $null)
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
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline -and @(Select-String -Path $captureLog -Pattern 'Saved ').Count -lt $sources.Count) { Start-Sleep -Milliseconds 250 }
    $recordings = @(Get-ChildItem $work -Filter *.mp4)
    Assert-That 'an MP4 file was saved for every stream' ($recordings.Count -eq $sources.Count -and -not ($recordings | Where-Object Length -lt 20KB)) "found: $(($recordings | ForEach-Object { "$($_.Name) $($_.Length) bytes" }) -join ', ')"

    Write-Host 'Frame rate'
    Set-Field 'frame-rate' '10'
    Assert-Eventually 'the capture client reports 10 fps' "$state.video.frameRate === 10"
    Start-Sleep -Seconds 5
    $fps = [double](Get-Js "$stats.video.framesPerSecond")
    Assert-That 'the page receives about 10 fps' ($fps -ge 8 -and $fps -le 12) "measured $fps fps"

    Write-Host 'JPEG pictures over the WebSocket'
    Set-Field 'video-mode' 'jpeg'
    foreach ($id in $sources) {
        Assert-Eventually "pictures of $id are shown" "${stats}?.sources?.['$id']?.codec === 'JPEG' && $stats.sources['$id'].framesDecoded > 10" 20
    }
    if ($hasAudio) {
        Assert-Eventually 'PCM audio arrives' "${stats}?.audio?.codec?.startsWith('PCM') && $stats.audio.packetsReceived > 50"

        Write-Host 'Audio source'
        Set-Field 'audio-source' 'desktop'
        Assert-Eventually 'desktop audio is selected' "$state.audio.source === 'desktop'"
        Set-Field 'audio-source' 'both'
        Assert-Eventually 'microphone and desktop audio are mixed' "$state.audio.source === 'both'"
        Set-Field 'audio-source' 'microphone'
        Assert-Eventually 'the microphone is selected again' "$state.audio.source === 'microphone'"
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

    Assert-That 'the page shows no control error' ((Get-Js "document.getElementById('control-note').dataset.kind") -ne 'error') (Get-Js "document.getElementById('control-note').textContent")

    foreach ($socket in $script:pages.Values) { $socket.Dispose() }
}
finally {
    foreach ($process in $processes) {
        if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
    }
    # Edge starts helper processes of its own; they all carry the profile folder on their command line.
    Get-CimInstance Win32_Process -Filter "Name = 'msedge.exe'" | Where-Object { $_.CommandLine -like "*$work*" } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

    if ($script:failures -gt 0 -and (Test-Path $captureLog)) {
        Write-Host '--- capture client log ---'
        Get-Content $captureLog | Select-Object -Last 40 | Write-Host
    }

    if ($KeepOutput) {
        Write-Host "Output kept in $work"
    }
    else {
        Start-Sleep -Milliseconds 500
        Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host $(if ($script:failures -eq 0) { 'All checks passed.' } else { "$script:failures check(s) failed." })
exit $script:failures
