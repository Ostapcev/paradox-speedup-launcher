"""Build the release assets for the launcher.

Usage:
    python tools/make_release.py GAME_ID PACKAGE_DIR [OUT_DIR]
    python tools/make_release.py --catalog [OUT_DIR]

GAME_ID is a folder in games/ (for example eu4). PACKAGE_DIR is the folder whose contents
go into the game folder. The script checks that every file named in
games/GAME_ID/manifest_src.json exists there, then writes these files to OUT_DIR
(default: release/):
- GAME_ID_plugins.zip;
- GAME_ID_manifest.json, with the SHA-256 of the zip and of every file;
- catalog.json, rebuilt from catalog_src.json.

Upload everything in release/ (except the exe, if you like) to one GitHub release.
The launcher reads https://github.com/OWNER/REPO/releases/latest/download/catalog.json
and finds the other files next to it.
"""
import hashlib
import json
import os
import sys
import zipfile

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def write_json(path, data):
    with open(path, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(data, f, ensure_ascii=False, indent=1)


def build_catalog(out):
    with open(os.path.join(HERE, 'catalog_src.json'), encoding='utf-8') as f:
        catalog = json.load(f)
    for game in catalog['games']:
        path = os.path.join(out, game.get('manifest') or '-')
        if game.get('status') == 'available' and not os.path.isfile(path):
            raise SystemExit(f'{game["id"]}: {path} is missing, run make_release.py {game["id"]} PACKAGE_DIR first')
        if os.path.isfile(path):
            with open(path, encoding='utf-8') as f:
                m = json.load(f)
            game['plugins_version'] = m.get('version')
            game['game_version'] = m.get('game_version')
    write_json(os.path.join(out, 'catalog.json'), catalog)
    print('catalog.json:', ', '.join(g['id'] + '=' + g.get('status', '?') for g in catalog['games']))


def build_game(game_id, package, out):
    with open(os.path.join(HERE, 'games', game_id, 'manifest_src.json'), encoding='utf-8') as f:
        manifest = json.load(f)
    names = []
    for component in manifest['components']:
        for name in component.get('files', []) + ([component['ini']] if component.get('ini') else []):
            if name not in names:
                names.append(name)
    missing = [n for n in names if not os.path.isfile(os.path.join(package, n))]
    if missing:
        raise SystemExit('Missing in package: ' + ', '.join(missing))

    hashes = {}
    manifest['package'] = game_id + '_plugins.zip'
    zip_path = os.path.join(out, manifest['package'])
    with zipfile.ZipFile(zip_path, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for name in names:
            with open(os.path.join(package, name), 'rb') as f:
                data = f.read()
            hashes[name] = sha256(data)
            info = zipfile.ZipInfo(name, date_time=(2026, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            z.writestr(info, data)
    with open(zip_path, 'rb') as f:
        manifest['package_sha256'] = sha256(f.read())
    manifest['file_sha256'] = hashes
    write_json(os.path.join(out, game_id + '_manifest.json'), manifest)
    print(f'{game_id}: {len(names)} files -> {zip_path} ({os.path.getsize(zip_path)} bytes)')


def main() -> None:
    args = sys.argv[1:]
    if not args:
        raise SystemExit(__doc__)
    if args[0] == '--catalog':
        out = os.path.abspath(args[1]) if len(args) > 1 else os.path.join(HERE, 'release')
    else:
        if len(args) < 2:
            raise SystemExit(__doc__)
        out = os.path.abspath(args[2]) if len(args) > 2 else os.path.join(HERE, 'release')
        os.makedirs(out, exist_ok=True)
        build_game(args[0], os.path.abspath(args[1]), out)
    build_catalog(out)


if __name__ == '__main__':
    main()
