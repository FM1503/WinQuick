# 执行书：CI 修复——vpk download/upload 补 --channel win-x64（第三次运行确诊）

背景：vpk upload 报 FTL「Could not find assets file for channel 'win' (looking for 'assets.win.json')…verify you're calling this command with the same '--channel' as you did with 'pack'」——download/upload 未指定 channel 时默认 OS 名 win，与 pack 的 win-x64 不一致。

## R1 .github/workflows/release.yml（两行命令替换）
old:
```
          vpk download github --repoUrl https://github.com/FM1503/WinQuick --token ${{ secrets.GITHUB_TOKEN }}
          vpk pack -u WinQuick -v ${{ steps.ver.outputs.version }} -p publish --channel win-x64 --mainExe WinQuick.exe
          vpk upload github --repoUrl https://github.com/FM1503/WinQuick --publish --releaseName "WinQuick ${{ steps.ver.outputs.version }}" --tag $env:GITHUB_REF_NAME --token ${{ secrets.GITHUB_TOKEN }}
```
new:
```
          vpk download github --repoUrl https://github.com/FM1503/WinQuick --channel win-x64 --token ${{ secrets.GITHUB_TOKEN }}
          vpk pack -u WinQuick -v ${{ steps.ver.outputs.version }} -p publish --channel win-x64 --mainExe WinQuick.exe
          vpk upload github --repoUrl https://github.com/FM1503/WinQuick --channel win-x64 --publish --releaseName "WinQuick ${{ steps.ver.outputs.version }}" --tag $env:GITHUB_REF_NAME --token ${{ secrets.GITHUB_TOKEN }}
```

## 验证
1. `python -c "import yaml,io; yaml.safe_load(io.open('.github/workflows/release.yml', encoding='utf-8')); print('yaml ok')"` → yaml ok；
2. `git diff` 仅此一个 hunk。
