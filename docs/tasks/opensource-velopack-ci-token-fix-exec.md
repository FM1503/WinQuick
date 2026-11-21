# 执行书：CI 修复——vpk upload 显式 --token（第二次运行 44855790720 确诊）

背景：vpk upload github 报 FTL「token is required when uploading to GitHub」——vpk 不读 GITHUB_TOKEN 环境变量，须显式 --token。download/pack 均正常（首版 No releases 属警告性中止）。

## R1 .github/workflows/release.yml（vpk 步骤整块替换）
old:
```
        run: |
          dotnet tool install -g vpk
          vpk download github --repoUrl https://github.com/FM1503/WinQuick
          vpk pack -u WinQuick -v ${{ steps.ver.outputs.version }} -p publish --channel win-x64 --mainExe WinQuick.exe
          vpk upload github --repoUrl https://github.com/FM1503/WinQuick --publish --releaseName "WinQuick ${{ steps.ver.outputs.version }}" --tag $env:GITHUB_REF_NAME
        env:
          GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}
```
new:
```
        run: |
          dotnet tool install -g vpk
          vpk download github --repoUrl https://github.com/FM1503/WinQuick --token ${{ secrets.GITHUB_TOKEN }}
          vpk pack -u WinQuick -v ${{ steps.ver.outputs.version }} -p publish --channel win-x64 --mainExe WinQuick.exe
          vpk upload github --repoUrl https://github.com/FM1503/WinQuick --publish --releaseName "WinQuick ${{ steps.ver.outputs.version }}" --tag $env:GITHUB_REF_NAME --token ${{ secrets.GITHUB_TOKEN }}
```

## 验证
1. `python -c "import yaml,io; yaml.safe_load(io.open('.github/workflows/release.yml', encoding='utf-8')); print('yaml ok')"` → yaml ok；
2. `git diff` 仅此一个 hunk。
