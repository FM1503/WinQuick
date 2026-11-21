# 执行书：CI 修复——checkout 拉取 LFS（v1.1.0 首跑 Publish 失败）

背景：actions/runs/44855342951 的 Publish 步骤失败。assets/winquick.ico 是 LFS 文件（ApplicationIcon 引用），actions/checkout@v4 默认 lfs:false 只检出指针文本，dotnet publish 找不到图标而失败；本地 smudge 正常所以从未暴露。

## R1 .github/workflows/release.yml
old:
```
      - name: Checkout
        uses: actions/checkout@v4
```
new:
```
      - name: Checkout
        uses: actions/checkout@v4
        with:
          # assets/winquick.ico 走 LFS：默认 checkout 只检出指针文本，
          # publish 会因找不到图标失败（v1.1.0 首跑实测）。
          lfs: true
```

## 验证
1. `python -c "import yaml,io; yaml.safe_load(io.open('.github/workflows/release.yml', encoding='utf-8')); print('yaml ok')"` → yaml ok；
2. `git diff` 仅此一个 hunk。
