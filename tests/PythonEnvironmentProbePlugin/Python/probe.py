import json
import os
import platform
import sys
import time
from pathlib import Path

import idna


def convert(domains):
    results = []
    for domain in domains:
        try:
            ascii_domain = idna.encode(domain).decode("ascii")
            results.append({"domain": domain, "ascii": ascii_domain, "error": None})
        except idna.IDNAError as error:
            results.append({"domain": domain, "ascii": None, "error": str(error)})
    return results


def main():
    request_path, result_path = map(Path, sys.argv[1:])
    request = json.loads(request_path.read_text(encoding="utf-8"))
    print(f"Python {platform.python_version()} / idna {idna.__version__} / PID {os.getpid()}", flush=True)
    print(f"解释器：{sys.executable}", flush=True)
    if request["expectedFailure"]:
        print("PYTHON_PROBE_EXPECTED_FAILURE：按所选场景主动失败。", file=sys.stderr, flush=True)
        return 17

    iterations = request["iterations"]
    for step in range(1, iterations + 1):
        results = convert(request["domains"])
        valid = sum(row["error"] is None for row in results)
        print(f"第 {step}/{iterations} 轮 · PID {os.getpid()} · {valid} 个有效域名 / {len(results)} 条输入", flush=True)
        if iterations > 1:
            time.sleep(1)

    report = {
        "pythonVersion": platform.python_version(),
        "dependencyVersion": idna.__version__,
        "pythonPath": sys.executable,
        "pid": os.getpid(),
        "iterations": iterations,
        "results": results,
    }
    result_path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print("处理完成，结果已写入插件报告目录。", flush=True)
    print("中文诊断输出：测试", file=sys.stderr, flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
