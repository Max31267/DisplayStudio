"""Package a published Windows app and prebuilt firmware, without user settings."""
from pathlib import Path
from hashlib import sha256
import json, shutil, zipfile, argparse
from markdown_it import MarkdownIt

repo = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument('--stage', required=True, help='Directory containing the published App folder')
parser.add_argument('--firmware-directory', default=str(repo / 'artifacts/firmware-release'))
args = parser.parse_args()
stage = Path(args.stage).resolve()
app = stage / 'App'
if not (app / 'MiniDisplay.exe').is_file():
    raise SystemExit('Publish the application into <stage>/App first')
version = '0.4.1'
for folder in ['Firmware', 'Examples', 'Licenses', 'App/Firmware', 'App/Tools/esptool', 'App/Docs']:
    (stage / folder).mkdir(parents=True, exist_ok=True)

tool_source = repo / '.tools/esptool-5.6.0/esptool-windows-amd64'
shutil.copy2(tool_source / 'esptool.exe', app / 'Tools/esptool/esptool.exe')
shutil.copy2(tool_source / 'LICENSE', app / 'Tools/esptool/LICENSE')
shutil.copy2(tool_source / 'README.md', app / 'Tools/esptool/README.md')
shutil.copy2(repo / '.tools/esptool-v5.6.0-source.tar.gz', stage / 'Licenses/esptool-v5.6.0-source.tar.gz')
shutil.copy2(tool_source / 'LICENSE', stage / 'Licenses/esptool-LICENSE.txt')
idf_root = Path((repo / '.tools/firmware-build-root.txt').read_text(encoding='utf-8-sig').strip()) / 'esp-idf'
shutil.copy2(idf_root / 'LICENSE', stage / 'Licenses/ESP-IDF-LICENSE.txt')
freertos = idf_root / 'components/freertos/FreeRTOS-Kernel/LICENSE.md'
if freertos.is_file():
    shutil.copy2(freertos, stage / 'Licenses/FreeRTOS-LICENSE.md')
project_marker = repo / '.tools/firmware-project-root.txt'
firmware_project = Path(project_marker.read_text(encoding='utf-8-sig').strip()) if project_marker.is_file() else idf_root.parent / 'project'
description = firmware_project / 'build-default/project_description.json'
for component in json.loads(description.read_text(encoding='utf-8'))['build_components']:
    for notice in (idf_root / 'components' / component).rglob('*'):
        if notice.is_file() and notice.name.lower().startswith(('license', 'copying', 'notice')):
            target = stage / 'Licenses/ESP-IDF-components' / notice.relative_to(idf_root / 'components')
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(notice, target)
compiler_licenses = idf_root.parent / 'tools/tools/riscv32-esp-elf/esp-14.2.0_20241119/riscv32-esp-elf/share/licenses'
for name in ['newlib/COPYING.NEWLIB', 'newlib/COPYING.LIBGLOSS', 'gcc/COPYING.RUNTIME', 'gcc/COPYING3']:
    source = compiler_licenses / name
    if source.is_file():
        target = stage / 'Licenses/Toolchain' / name
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, target)
(stage / 'Licenses/README.txt').write_text('esptool 5.6.0: GPL-2.0-or-later. Full upstream source is included in this folder.\nESP-IDF v5.5: https://github.com/espressif/esp-idf/tree/v5.5\nApp dependencies: see App/THIRD-PARTY.md and App/licenses.\n', encoding='utf-8')

hash_file = lambda path: sha256(path.read_bytes()).hexdigest()
profiles = [
    ('y0', 'Стандартный ST7789', 'st7789-y0.bin'),
    ('y80', 'Смещение 80 строк', 'st7789-y80.bin'),
    ('y0-bgr', 'Красный и синий поменяны', 'st7789-y0-bgr.bin'),
    ('y80-bgr', 'Смещение 80 строк + замена цветов', 'st7789-y80-bgr.bin'),
]
manifest_profiles = []
for profile_id, name, filename in profiles:
    source = Path(args.firmware_directory).resolve() / filename
    data = source.read_bytes()
    if len(data) < 24 or len(data) > 4 * 1024 * 1024 or data[0] != 0xe9:
        raise SystemExit(f'Invalid merged firmware: {filename}')
    for target in [stage / 'Firmware' / filename, app / 'Firmware' / filename]:
        shutil.copy2(source, target)
    manifest_profiles.append(dict(Id=profile_id, Name=name, File='Firmware/' + filename, Sha256=hash_file(source)))
manifest = dict(Version=version, Chip='esp32c3', Tool='Tools/esptool/esptool.exe', ToolSha256=hash_file(app / 'Tools/esptool/esptool.exe'), Profiles=manifest_profiles)
(app / 'Firmware/manifest.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding='utf-8')

with zipfile.ZipFile(stage / 'Firmware/source.zip', 'w', zipfile.ZIP_DEFLATED) as archive:
    source_root = repo / 'firmware'
    for name in ['CMakeLists.txt', 'sdkconfig.defaults', 'README.md', 'main', 'portable', 'profiles']:
        path = source_root / name
        files = path.rglob('*') if path.is_dir() else [path]
        for file in files:
            if file.is_file():
                archive.write(file, file.relative_to(source_root).as_posix())
    archive.write(repo / 'scripts/build-firmware.ps1', 'build-firmware.ps1')
shutil.copy2(repo / 'firmware/README.md', stage / 'Firmware/README.md')
for path in (repo / 'examples').glob('*'):
    if path.is_file():
        shutil.copy2(path, stage / 'Examples' / path.name)

wiring = (repo / 'docs/wiring.svg').read_text(encoding='utf-8')
guide = (repo / 'docs/user-guide.md').read_text(encoding='utf-8')
body = MarkdownIt('commonmark', {'html':False}).enable('table').render(guide)
css = 'body{margin:0;background:#f5f7fc;color:#243247;font:16px/1.65 Segoe UI,Arial,sans-serif}main{max-width:1080px;margin:32px auto;padding:36px 48px;background:white;border:1px solid #e1e7f1;border-radius:18px}h1{color:#3658ff;font-size:36px;line-height:1.2}h2{margin-top:40px;color:#17243a;line-height:1.3}h3{margin-top:28px}a{color:#3153e8}code{background:#eef2fa;padding:2px 5px;border-radius:4px;overflow-wrap:anywhere}pre{background:#eef2fa;padding:18px;overflow:auto;border-radius:9px}pre code{padding:0}table{border-collapse:collapse;width:100%;margin:18px 0;font-size:14px}td,th{border:1px solid #dce4ef;padding:10px 12px;text-align:left;vertical-align:top;overflow-wrap:anywhere}th{background:#eef2ff}img{max-width:100%;height:auto}li{margin:8px 0}blockquote{border-left:4px solid #3658ff;margin:18px 0;padding:4px 18px;background:#f5f7ff}@media(max-width:720px){main{margin:0;padding:22px;border:0;border-radius:0}table{font-size:12px}td,th{padding:7px}}@media print{body{background:white}main{border:0;margin:0;padding:0}h2{break-after:avoid}tr,img{break-inside:avoid}}'
page = f'<!doctype html><html lang="ru"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Display Studio — установка и подключение</title><style>{css}</style></head><body><main>{body}</main></body></html>'
for root in [stage, app / 'Docs']:
    (root / 'README.html').write_text(page, encoding='utf-8')
    (root / 'wiring.svg').write_text(wiring, encoding='utf-8')
(stage / 'README.md').write_text(guide, encoding='utf-8')
(stage / 'README.txt').write_text(guide, encoding='utf-8-sig')
(stage / 'Start Display Studio.cmd').write_bytes(b'@echo off\r\nstart "" "%~dp0App\\MiniDisplay.exe"\r\n')
(stage / 'VERSION.txt').write_text(f'Display Studio {version}\nWindows x64 / ESP32-C3 / ST7789 240x240\nESP-IDF v5.5 / esptool v5.6.0\nHardware testing: pending (no board available)\n', encoding='utf-8')
# Debug symbols are not needed by the recipient and contain local build paths.
for pdb in app.rglob('*.pdb'):
    pdb.unlink()
for file in app.rglob('settings.json'):
    raise SystemExit(f'Unexpected local settings in release: {file}')
checksums = '\n'.join(f'{hash_file(file)}  {file.relative_to(stage).as_posix()}' for file in sorted(stage.rglob('*')) if file.is_file() and file.name != 'SHA256SUMS.txt') + '\n'
(stage / 'SHA256SUMS.txt').write_text(checksums, encoding='utf-8')
archive_path = stage.parent / (stage.name + '-win-x64-esp32c3.zip')
with zipfile.ZipFile(archive_path, 'w', zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
    for file in stage.rglob('*'):
        if file.is_file():
            archive.write(file, (Path(stage.name) / file.relative_to(stage)).as_posix())
(archive_path.parent / (archive_path.name+'.sha256')).write_text(hash_file(archive_path)+'  '+archive_path.name+'\n', encoding='ascii')
print(json.dumps(dict(archive=str(archive_path), bytes=archive_path.stat().st_size, files=len(checksums.splitlines()), firmware=len(profiles)), ensure_ascii=False))
