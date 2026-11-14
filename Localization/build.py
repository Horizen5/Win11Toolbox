"""
从 translations.tsv 生成运行时词典 Localization/locales.json。

- 9 种外语：核心 UI 文案（TSV 里逐条人工翻译）
- 繁体中文：对源码里全部中文串做简→繁转换 + 台湾用语替换，覆盖率 100%
- 简体中文：空字典，运行时直接用源码里的原文

用法（需要 zhconv）：
    python -m pip install zhconv
    python Localization/build.py

改完 translations.tsv 重跑一次即可，C# 侧不用动。
"""
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)

# zhconv 只用于构建阶段；没装时给出可操作的提示，不静默产出半份词典
try:
    import zhconv
except ImportError:
    sys.exit("缺少 zhconv，请先执行：python -m pip install zhconv")

TSV = os.path.join(HERE, "translations.tsv")
OUT = os.path.join(HERE, "locales.json")

LANGS = [
    ("auto", "自动（跟随系统）", "Auto (system)"),
    ("zh-CN", "简体中文", "Chinese (Simplified)"),
    ("en-US", "English", "English (United States)"),
    ("zh-TW", "繁體中文", "Chinese (Traditional)"),
    ("ja-JP", "日本語", "Japanese"),
    ("ko-KR", "한국어", "Korean"),
    ("es-ES", "Español", "Spanish"),
    ("fr-FR", "Français", "French"),
    ("de-DE", "Deutsch", "German"),
    ("ru-RU", "Русский", "Russian"),
    ("pt-BR", "Português (Brasil)", "Portuguese (Brazil)"),
]
CODES = ["en-US", "ja-JP", "ko-KR", "es-ES", "fr-FR", "de-DE", "ru-RU", "pt-BR"]

# 大陆→台湾用语。在简繁转换之后按「长词优先」逐条替换。
# 左侧必须是「转换后可能出现的繁体写法」，右侧是台湾惯用词。
TW_TERMS = [
    ("資源回收筒", "資源回收筒"),
    ("回收站", "資源回收筒"),
    ("資源管理器", "檔案總管"),
    ("文件夾", "資料夾"),
    ("檔案夾", "資料夾"),
    ("註冊表", "登錄"),
    ("注册表", "登錄"),
    ("登錄表", "登錄"),
    ("註冊", "登錄"),
    ("開始菜單", "開始功能表"),
    ("任務欄", "工作列"),
    ("任務列", "工作列"),
    ("驅動器", "磁碟機"),
    ("磁盤", "磁碟"),
    ("內存", "記憶體"),
    ("顯卡", "顯示卡"),
    ("主板", "主機板"),
    ("托盤", "系統匣"),
    ("粘貼", "貼上"),
    ("圖標", "圖示"),
    ("圖示", "圖示"),
    ("運行庫", "執行階段程式庫"),
    ("賬戶", "帳戶"),
    ("緩存", "快取"),
    ("信息", "資訊"),
    ("資訊", "資訊"),
    ("網絡", "網路"),
    ("网络", "網路"),
    ("設置", "設定"),
    ("设置", "設定"),
    ("軟件", "軟體"),
    ("軟件", "軟體"),
    ("硬件", "硬體"),
    ("程序", "程式"),
    ("默認", "預設"),
    ("默認值", "預設值"),
    ("界面", "介面"),
    ("文本", "文字"),
    ("窗口", "視窗"),
    ("端口", "連接埠"),
    ("哈希", "雜湊"),
    ("校驗", "驗證"),
    ("生成", "產生"),
    ("檢測", "偵測"),
    ("日誌", "記錄"),
    ("優化", "最佳化"),
    ("高性能", "高效能"),
    ("節能", "省電"),
    ("消息", "訊息"),
    ("數據", "資料"),
    ("此電腦", "本機"),
    ("回收筒", "資源回收筒"),
    ("文件", "檔案"),
]


def to_tw(text: str) -> str:
    s = zhconv.convert(text, "zh-tw")
    for a, b in TW_TERMS:
        if a in s:
            s = s.replace(a, b)
    return s


def collect_source_strings() -> set[str]:
    """扫描源码里所有含中文的字符串字面量，供繁中全量转换使用。"""
    han = re.compile(r"[\u4e00-\u9fff]")
    attr = re.compile(
        r'\b(?:Text|Content|Title|Header|ToolTip|PlaceholderText|Watermark|Description|Label)="([^"]+)"'
    )
    lit = re.compile(r'"([^"\\\n]*(?:\\.[^"\\\n]*)*)"')
    found = set()
    for base, dirs, files in os.walk(ROOT):
        dirs[:] = [d for d in dirs if d not in ("obj", "bin", ".git", "Simple")
                   and not d.startswith("publish")]
        for name in files:
            if not name.endswith((".cs", ".xaml")):
                continue
            path = os.path.join(base, name)
            try:
                src = open(path, encoding="utf-8-sig").read()
            except OSError:
                continue
            for m in attr.finditer(src):
                if han.search(m.group(1)):
                    found.add(m.group(1))
            if name.endswith(".cs"):
                for m in lit.finditer(src):
                    if han.search(m.group(1)):
                        found.add(m.group(1))
    return found


def main():
    rows = []
    for line in open(TSV, encoding="utf-8-sig").read().splitlines():
        if not line.strip():
            continue
        parts = line.split("|")
        if len(parts) != 9:
            sys.exit(f"列数应为 9（中文 + 8 种外语），实际 {len(parts)}：{line[:60]}")
        rows.append(parts)

    keys = [r[0] for r in rows]
    if len(keys) != len(set(keys)):
        sys.exit("translations.tsv 里有重复词条")

    translations = {"zh-CN": {}}
    for i, code in enumerate(CODES, start=1):
        translations[code] = {r[0]: r[i] for r in rows if r[i]}

    # 繁中：源码里所有中文串全量转换（含长尾描述、工具名、开关说明）
    all_keys = collect_source_strings() | set(keys)
    tw = {}
    for k in sorted(all_keys):
        v = to_tw(k)
        if v != k:
            tw[k] = v
    translations["zh-TW"] = tw

    payload = {
        "version": 1,
        "languages": [
            {"code": c, "native": n, "english": e} for c, n, e in LANGS
        ],
        "counts": {c: len(v) for c, v in translations.items()},
        "translations": translations,
    }
    with open(OUT, "w", encoding="utf-8") as f:
        json.dump(payload, f, ensure_ascii=False, indent=1)

    print("已生成", OUT)
    print("文件大小", round(os.path.getsize(OUT) / 1024, 1), "KB")
    for c, v in translations.items():
        print(f"  {c:6} {len(v):5} 条")


if __name__ == "__main__":
    main()
