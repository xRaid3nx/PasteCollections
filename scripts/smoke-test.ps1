<#
.SYNOPSIS
  End-to-end test of Paste Collections against a throwaway Jellyfin instance.

.DESCRIPTION
  Starts a SEPARATE Jellyfin process from your install (default D:\Apps\Jellyfin\jellyfin.exe) with its own data
  folder under artifacts\sandbox and its own port (8196), so your real server, settings and libraries are untouched.
  It creates tiny fake media files, runs the startup wizard, scans, then checks:
    - the plugin loads as Active and its dashboard page is registered in the main menu
    - Preview statuses (exact year, ambiguous, bare-year fallback, name variants, not found, series)
    - Apply creates the collection, a second Apply adds nothing (idempotent)
    - non-admin and anonymous callers are rejected
  The sandbox server is stopped at the end. Use -Keep to leave it running for a manual look in the browser.

.EXAMPLE
  .\scripts\smoke-test.ps1
#>
[CmdletBinding()]
param(
  [string]$JellyfinDir = 'D:\Apps\Jellyfin',
  [int]$Port = 8196,
  [string]$JellyfinVersion = '12.2.0',
  [switch]$Keep
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$sandbox = Join-Path $repoRoot 'artifacts\sandbox'
$base = "http://localhost:$Port"
$pluginGuid = 'd7904f73-f7e5-4b0a-ae99-a33d1afd11b1'
$authBase = 'MediaBrowser Client="PasteCollectionsSmokeTest", Device="smoke", DeviceId="paste-collections-smoke", Version="1.0"'
$script:failures = 0

function Step([string]$m) { Write-Host "==> $m" -ForegroundColor Cyan }
function Check([bool]$ok, [string]$what) {
  if ($ok) { Write-Host "    PASS  $what" -ForegroundColor Green }
  else { Write-Host "    FAIL  $what" -ForegroundColor Red; $script:failures++ }
}
function Api([string]$method, [string]$path, $body = $null, [string]$token = '') {
  $headers = @{ Authorization = if ($token) { "$authBase, Token=`"$token`"" } else { $authBase } }
  $params = @{ Method = $method; Uri = "$base/$path"; Headers = $headers; UseBasicParsing = $true }
  if ($null -ne $body) { $params.Body = [Text.Encoding]::UTF8.GetBytes(($body | ConvertTo-Json -Depth 10)); $params.ContentType = 'application/json' }
  $response = Invoke-WebRequest @params
  if ($response.Content) { return $response.Content | ConvertFrom-Json }
  return $null
}
function StatusOf([string]$method, [string]$path, $body = $null, [string]$token = '') {
  try { Api $method $path $body $token | Out-Null; return 200 }
  catch { if ($_.Exception.Response) { return [int]$_.Exception.Response.StatusCode } else { throw } }
}
function NewFakeMedia([string]$path) {
  New-Item -ItemType Directory -Force -Path (Split-Path $path) | Out-Null
  $bytes = New-Object byte[] 2048
  (New-Object Random).NextBytes($bytes)
  [IO.File]::WriteAllBytes($path, $bytes)
}

if (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue) { throw "Port $Port is in use. Pass -Port." }

# ---- Sandbox ---------------------------------------------------------------------
Step "Preparing sandbox at $sandbox"
if (Test-Path $sandbox) { Remove-Item -Recurse -Force $sandbox }
$data = Join-Path $sandbox 'data'
New-Item -ItemType Directory -Force -Path (Join-Path $data 'config'), (Join-Path $data 'plugins') | Out-Null
@"
<?xml version="1.0" encoding="utf-8"?>
<NetworkConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <EnableHttps>false</EnableHttps>
  <InternalHttpPort>$Port</InternalHttpPort>
  <PublicHttpPort>$Port</PublicHttpPort>
  <InternalHttpsPort>$($Port + 1)</InternalHttpsPort>
  <PublicHttpsPort>$($Port + 1)</PublicHttpsPort>
  <AutoDiscovery>false</AutoDiscovery>
  <EnableUPnP>false</EnableUPnP>
</NetworkConfiguration>
"@ | Set-Content -Path (Join-Path $data 'config\network.xml') -Encoding UTF8

$movies = Join-Path $sandbox 'media\Movies'
$shows = Join-Path $sandbox 'media\Shows'
foreach ($m in 'The Lion King (1994)', 'The Lion King (2019)', 'Aladdin (1992)', 'Aladdin (2019)', 'Moana (2016)', 'Frozen II (2019)', 'WALL-E (2008)', 'Coco (2017)', 'Up (2009)') {
  NewFakeMedia (Join-Path $movies "$m\$m.mkv")
}
NewFakeMedia (Join-Path $shows 'DuckTales (2017)\Season 1\DuckTales S01E01.mkv')

# Local posters for the collage check (the libraries fetch nothing from the internet).
Add-Type -AssemblyName System.Drawing
$posterColors = @{ 'Moana (2016)' = 'SteelBlue'; 'Coco (2017)' = 'DarkOrange'; 'Up (2009)' = 'SeaGreen'; 'WALL-E (2008)' = 'Firebrick' }
foreach ($m in $posterColors.Keys) {
  $bitmap = New-Object System.Drawing.Bitmap 400, 600
  $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
  $graphics.Clear([System.Drawing.Color]::FromName($posterColors[$m]))
  $graphics.Dispose()
  $bitmap.Save((Join-Path $movies "$m\poster.jpg"), [System.Drawing.Imaging.ImageFormat]::Jpeg)
  $bitmap.Dispose()
  $bitmap = New-Object System.Drawing.Bitmap 1280, 720
  $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
  $graphics.Clear([System.Drawing.Color]::FromName($posterColors[$m]))
  $graphics.Dispose()
  $bitmap.Save((Join-Path $movies "$m\backdrop.jpg"), [System.Drawing.Imaging.ImageFormat]::Jpeg)
  $bitmap.Dispose()
}

Step 'Building and installing the plugin into the sandbox'
& dotnet build (Join-Path $repoRoot 'src\Jellyfin.Plugin.Collections\Jellyfin.Plugin.Collections.csproj') -c Release "-p:JellyfinVersion=$JellyfinVersion" --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
$pluginDir = Join-Path $data 'plugins\Paste Collections_1.0.0.0'
New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null
Copy-Item (Join-Path $repoRoot 'src\Jellyfin.Plugin.Collections\bin\Release\net10.0\Jellyfin.Plugin.Collections.dll') $pluginDir

# ---- Start --------------------------------------------------------------------------
Step "Starting sandbox Jellyfin on port $Port"
$exe = Join-Path $JellyfinDir 'jellyfin.exe'
$proc = Start-Process -FilePath $exe -PassThru -WindowStyle Hidden -WorkingDirectory $JellyfinDir -ArgumentList @(
  '--datadir', "`"$data`"", '--cachedir', "`"$(Join-Path $sandbox 'cache')`"", '--logdir', "`"$(Join-Path $sandbox 'log')`"",
  '--webdir', "`"$(Join-Path $JellyfinDir 'jellyfin-web')`"", '--ffmpeg', "`"$(Join-Path $JellyfinDir 'ffmpeg.exe')`"")

try {
  $deadline = (Get-Date).AddMinutes(3)
  # Jellyfin 12 answers System/Info/Public from a temporary startup server while migrating; wait for the real one.
  while ($true) {
    try { Api GET 'Startup/Configuration' | Out-Null; $info = Api GET 'System/Info/Public'; break } catch { }
    if ($proc.HasExited) { throw "Sandbox Jellyfin exited (code $($proc.ExitCode)). See $sandbox\log." }
    if ((Get-Date) -gt $deadline) { throw 'Sandbox Jellyfin did not start within 3 minutes.' }
    Start-Sleep -Seconds 2
  }
  Write-Host "    Server $($info.Version) is up"

  Step 'Running the startup wizard'
  Api POST 'Startup/Configuration' @{ UICulture = 'en-US'; MetadataCountryCode = 'US'; PreferredMetadataLanguage = 'en' } | Out-Null
  Api GET 'Startup/User' | Out-Null
  Api POST 'Startup/User' @{ Name = 'admin'; Password = 'smoke-test' } | Out-Null
  Api POST 'Startup/Complete' | Out-Null
  $auth = Api POST 'Users/AuthenticateByName' @{ Username = 'admin'; Pw = 'smoke-test' }
  $token = $auth.AccessToken

  Step 'Creating libraries (no internet metadata) and scanning'
  $noFetchers = @{ EnableRealtimeMonitor = $false; TypeOptions = @(
      @{ Type = 'Movie'; MetadataFetchers = @(); ImageFetchers = @() },
      @{ Type = 'Series'; MetadataFetchers = @(); ImageFetchers = @() },
      @{ Type = 'Season'; MetadataFetchers = @(); ImageFetchers = @() },
      @{ Type = 'Episode'; MetadataFetchers = @(); ImageFetchers = @() }) }
  Api POST ("Library/VirtualFolders?name=Movies&collectionType=movies&refreshLibrary=false&paths=" + [uri]::EscapeDataString($movies)) @{ LibraryOptions = $noFetchers } $token | Out-Null
  Api POST ("Library/VirtualFolders?name=Shows&collectionType=tvshows&refreshLibrary=true&paths=" + [uri]::EscapeDataString($shows)) @{ LibraryOptions = $noFetchers } $token | Out-Null

  $deadline = (Get-Date).AddMinutes(3)
  do {
    Start-Sleep -Seconds 3
    $count = (Api GET 'Items?Recursive=true&IncludeItemTypes=Movie,Series&Limit=0' $null $token).TotalRecordCount
  } while ($count -lt 10 -and (Get-Date) -lt $deadline)
  Check ($count -ge 10) "library scanned ($count of 10 movies/series)"
  # The first collection on a server makes Jellyfin create its Collections folder and rescan the library, and that scan
  # races with saving the collection. A warm-up collection gets this out of the way, like on a server that already has one.
  Api POST 'Collections?name=Smoke%20Warm-up' $null $token | Out-Null
  $deadline = (Get-Date).AddMinutes(2)
  do {
    Start-Sleep -Seconds 2
    $scan = (Api GET 'ScheduledTasks' $null $token) | Where-Object { $_.Key -eq 'RefreshLibrary' }
  } while ($scan.State -ne 'Idle' -and (Get-Date) -lt $deadline)

  Step 'Plugin registration'
  $plugin = (Api GET 'Plugins' $null $token) | Where-Object { ($_.Id -replace '-', '') -eq ($pluginGuid -replace '-', '') }
  Check ($null -ne $plugin -and $plugin.Status -eq 'Active') "plugin status is Active (got '$($plugin.Status)')"
  $pages = Api GET 'web/ConfigurationPages?enableInMainMenu=true' $null $token
  $page = $pages | Where-Object { $_.Name -eq 'PasteCollections' }
  Check ($null -ne $page) 'dashboard page is in the main menu'
  Check ($page.DisplayName -eq 'Paste Collections') "menu display name is 'Paste Collections'"
  $html = (Invoke-WebRequest -UseBasicParsing -Uri "$base/web/ConfigurationPage?name=PasteCollections" -Headers @{ Authorization = "$authBase, Token=`"$token`"" }).Content
  Check ($html -match 'PasteCollectionsPage') 'dashboard page HTML is served'

  Step 'Security'
  Check ((StatusOf GET 'PasteCollections/Collections') -eq 401) 'anonymous call is rejected (401)'
  $viewer = Api POST 'Users/New' @{ Name = 'viewer'; Password = 'viewer' } $token
  $viewerToken = (Api POST 'Users/AuthenticateByName' @{ Username = 'viewer'; Pw = 'viewer' }).AccessToken
  Check ((StatusOf GET 'PasteCollections/Collections' $null $viewerToken) -eq 403) 'non-admin call is rejected (403)'
  Check ((StatusOf POST 'PasteCollections/Apply' @{ CollectionName = 'x'; ItemIds = @() } $viewerToken) -eq 403) 'non-admin Apply is rejected (403)'

  Step 'Preview'
  $list = "# Disney test`r`nThe Lion King (1994)`r`nThe Lion King`r`nAladdin 1992`r`nFrozen 2`r`nWALL$([char]0x00B7)E`r`nMoana`r`nmoana`r`nTreasure Planet (2002)`r`nDuckTales"
  $preview = Api POST 'PasteCollections/Preview' @{ List = $list; CollectionName = 'Disney Classics'; MediaTypes = 'Both'; YearTolerance = 0; Fuzzy = $true } $token
  $byLine = @{}; $preview.Lines | ForEach-Object { $byLine[[int]$_.LineNumber] = $_ }
  Check ($byLine[2].Status -eq 'Matched') "'The Lion King (1994)' -> Matched"
  Check ($byLine[3].Status -eq 'Ambiguous' -and $byLine[3].Candidates.Count -eq 2) "'The Lion King' -> Ambiguous with 2 candidates"
  Check ($byLine[4].Status -eq 'Matched' -and $byLine[4].Candidates[0].Year -eq 1992) "'Aladdin 1992' -> Matched 1992"
  Check ($byLine[5].Status -eq 'Matched') "'Frozen 2' -> Matched (Frozen II)"
  Check ($byLine[6].Status -eq 'Matched') "'WALL-dot-E' -> Matched (WALL-E)"
  Check ($byLine[7].Status -eq 'Matched') "'Moana' -> Matched"
  Check (@($preview.DuplicateLines).Count -eq 1) "'moana' reported as a duplicate line"
  Check ($byLine[9].Status -eq 'NotFound') "'Treasure Planet' -> NotFound"
  Check ($byLine[10].Status -eq 'Matched' -and $byLine[10].Candidates[0].Kind -eq 'Series') "'DuckTales' -> Matched series"
  Check (-not $preview.Collection.Exists) 'collection does not exist yet'
  $ids = @($preview.Lines | Where-Object { $_.Selected } | ForEach-Object { $_.SelectedId })
  Check ($ids.Count -eq 6) "6 rows pre-selected (got $($ids.Count))"
  Check ((StatusOf POST 'PasteCollections/Preview' @{ List = ''; CollectionName = 'x' } $token) -eq 400) 'empty list -> 400'
  Check ((StatusOf POST 'PasteCollections/Preview' @{ List = 'Moana'; CollectionName = '   ' } $token) -eq 400) 'blank name -> 400'

  Step 'Apply'
  $apply = Api POST 'PasteCollections/Apply' @{ CollectionName = 'Disney Classics'; LockMetadata = $false; ItemIds = $ids + @([guid]::NewGuid().ToString()) } $token
  Check ($apply.Created -and $apply.Added -eq 6) "created collection with 6 items (created=$($apply.Created), added=$($apply.Added))"
  Check (@($apply.Failed).Count -eq 1) 'unknown id rejected and reported'
  $again = Api POST 'PasteCollections/Apply' @{ CollectionName = 'disney classics'; ItemIds = $ids } $token
  Check (-not $again.Created -and $again.Added -eq 0 -and $again.AlreadyPresent -eq 6) "re-run adds nothing (added=$($again.Added), alreadyPresent=$($again.AlreadyPresent))"
  $collections = Api GET 'PasteCollections/Collections' $null $token
  $dc = $collections | Where-Object { $_.Name -eq 'Disney Classics' }
  Check (@($dc).Count -eq 1 -and $dc.ItemCount -eq 6) "Collections endpoint lists 'Disney Classics' with 6 items"
  # Jellyfin saves collection members in the background, so give it a few seconds.
  for ($i = 0; $i -lt 20; $i++) {
    $members = Api GET ("Items?ParentId=$($apply.CollectionId)&UserId=$($auth.User.Id)") $null $token
    if ($members.TotalRecordCount -ge 6) { break }
    Start-Sleep -Milliseconds 500
  }
  Check ($members.TotalRecordCount -eq 6) "Jellyfin's own Items API shows 6 items in the collection (got $($members.TotalRecordCount))"
  $rerun = Api POST 'PasteCollections/Preview' @{ List = $list; CollectionName = 'Disney Classics'; MediaTypes = 'Both' } $token
  $already = @($rerun.Lines | Where-Object { $_.Status -eq 'AlreadyInCollection' }).Count
  Check ($already -eq 6) "second preview marks the 6 added rows as AlreadyInCollection (got $already)"
  Check (-not ($rerun.Lines | Where-Object { $_.Selected })) 'second preview pre-selects nothing'

  Step 'Poster for a new collection'
  $pixar = Api POST 'PasteCollections/Preview' @{ List = "Moana`nCoco`nUp`nWALL-E"; CollectionName = 'Smoke Poster Picks'; MediaTypes = 'Movies' } $token
  $pixarIds = @($pixar.Lines | Where-Object { $_.Selected } | ForEach-Object { $_.SelectedId })
  $posterApply = Api POST 'PasteCollections/Apply' @{ CollectionName = 'Smoke Poster Picks'; LockMetadata = $true; Poster = 'TmdbThenCollage'; ItemIds = $pixarIds } $token
  Write-Host "    Poster result: $($posterApply.Poster) - $($posterApply.PosterMessage)"
  Check ($posterApply.Created -and $posterApply.Poster -eq 'Collage') "new collection got a collage (got '$($posterApply.Poster)')"
  $item = Api GET ("Users/$($auth.User.Id)/Items/$($posterApply.CollectionId)") $null $token
  Check ($null -ne $item.ImageTags.Primary) 'collection has a primary image'
  Check ($item.LockData -eq $true) 'collection metadata is locked after the poster was added'
  $posterFile = Join-Path $sandbox 'collection-poster.jpg'
  Invoke-WebRequest -UseBasicParsing -Uri "$base/Items/$($posterApply.CollectionId)/Images/Primary" -OutFile $posterFile
  $image = [System.Drawing.Image]::FromFile($posterFile)
  $size = "$($image.Width)x$($image.Height)"
  $topLeft = ([System.Drawing.Bitmap]$image).GetPixel(20, 20)
  $bottomRight = ([System.Drawing.Bitmap]$image).GetPixel($image.Width - 20, $image.Height - 20)
  $image.Dispose()
  Check ($size -eq '600x900') "collage is poster-shaped 600x900 (got $size)"
  Check ($topLeft.ToArgb() -ne $bottomRight.ToArgb()) 'collage shows more than one poster'
  Check ($posterApply.Backdrop -eq 'Collage') "new collection got a backdrop collage (got '$($posterApply.Backdrop)')"
  Check (@($item.BackdropImageTags).Count -ge 1) 'collection has a backdrop'
  $backdropFile = Join-Path $sandbox 'collection-backdrop.jpg'
  Invoke-WebRequest -UseBasicParsing -Uri "$base/Items/$($posterApply.CollectionId)/Images/Backdrop/0" -OutFile $backdropFile
  $image = [System.Drawing.Image]::FromFile($backdropFile)
  $size = "$($image.Width)x$($image.Height)"
  $topLeft = ([System.Drawing.Bitmap]$image).GetPixel(40, 40)
  $bottomRight = ([System.Drawing.Bitmap]$image).GetPixel($image.Width - 40, $image.Height - 40)
  $image.Dispose()
  Check ($size -eq '1920x1080') "backdrop collage is 1920x1080 (got $size)"
  Check ($topLeft.ToArgb() -ne $bottomRight.ToArgb()) 'backdrop collage shows more than one backdrop'
  $offApply = Api POST 'PasteCollections/Apply' @{ CollectionName = 'Smoke No Poster'; Poster = 'Off'; ItemIds = @($pixarIds[0]) } $token
  Check ($offApply.Created -and $offApply.Poster -eq 'NotAttempted') "poster 'Off' leaves artwork alone (got '$($offApply.Poster)')"

  Step 'Artwork for existing collections'
  $missing = Api GET 'PasteCollections/Posters/Missing' $null $token
  Write-Host "    Missing artwork: $(($missing | ForEach-Object { $_.Name }) -join ', ')"
  Check (@($missing | Where-Object { $_.Id -eq $offApply.CollectionId }).Count -eq 1) 'collection without artwork is listed'
  Check (@($missing | Where-Object { $_.Id -eq $posterApply.CollectionId }).Count -eq 0) 'collection with a poster and backdrop is not listed'
  $fill = Api POST 'PasteCollections/Posters/Add' @{ CollectionId = $offApply.CollectionId; Poster = 'TmdbThenCollage' } $token
  Check ($fill.Poster -eq 'SinglePoster') "one-item collection got its item's poster (got '$($fill.Poster)': $($fill.Message))"
  Check ($fill.Backdrop -eq 'SingleBackdrop') "one-item collection got its item's backdrop (got '$($fill.Backdrop)')"
  $item = Api GET ("Users/$($auth.User.Id)/Items/$($offApply.CollectionId)") $null $token
  Check ($null -ne $item.ImageTags.Primary -and @($item.BackdropImageTags).Count -ge 1) 'existing collection now has a poster and a backdrop'
  $again = Api POST 'PasteCollections/Posters/Add' @{ CollectionId = $offApply.CollectionId; Poster = 'TmdbThenCollage' } $token
  Check ($again.Poster -eq 'NotAttempted' -and $again.Backdrop -eq 'NotAttempted') "a collection with full artwork is left alone (got '$($again.Poster)', '$($again.Backdrop)')"

  $log = Get-ChildItem (Join-Path $sandbox 'log') -Filter '*.log' | Sort-Object LastWriteTime | Select-Object -Last 1
  $pluginLines = Select-String -Path $log.FullName -Pattern 'Paste Collections' | Select-Object -First 8
  Step 'Plugin log lines'
  $pluginLines | ForEach-Object { Write-Host "    $($_.Line)" }
  $errors = Select-String -Path $log.FullName -Pattern '\[ERR\] \[\d+\] Jellyfin\.Plugin\.Collections'
  Check (-not $errors) 'no plugin errors in the server log'
}
finally {
  if ($Keep) {
    Write-Host "Sandbox left running at $base (admin / smoke-test). Stop it with: Stop-Process -Id $($proc.Id)" -ForegroundColor Yellow
  } elseif (-not $proc.HasExited) {
    Step 'Stopping sandbox Jellyfin'
    Stop-Process -Id $proc.Id -Force
  }
}

Write-Host ''
if ($script:failures -eq 0) { Write-Host 'SMOKE TEST PASSED' -ForegroundColor Green; exit 0 }
Write-Host "SMOKE TEST FAILED ($script:failures check(s))" -ForegroundColor Red
exit 1
