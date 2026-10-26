#!/usr/bin/env python3
"""Turns failed tests in TRX files into GitHub annotations, so a red run says which test and why."""
import glob
import sys
import xml.etree.ElementTree as ET

NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
failed = 0
seen = set()
for path in glob.glob(sys.argv[1] if len(sys.argv) > 1 else "**/TestResults/*.trx", recursive=True):
    root = ET.parse(path).getroot()
    for result in root.iterfind(".//t:UnitTestResult", NS):
        if result.get("outcome") != "Failed":
            continue
        failed += 1
        if failed > 10:
            continue
        message = result.find(".//t:Message", NS)
        stack = result.find(".//t:StackTrace", NS)
        first = (message.text if message is not None else "")[:200]
        if first in seen:
            continue
        seen.add(first)
        text = ((message.text if message is not None else "") + "\n" + (stack.text if stack is not None else ""))[:3000]
        text = text.replace("%", "%25").replace("\r", "").replace("\n", "%0A")
        print(f"::error title={result.get('testName')}::{text}")
print(f"{failed} failed tests", file=sys.stderr)
