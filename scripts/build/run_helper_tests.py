"""Run the actual helper suite and produce automatic, noninteractive build evidence."""
import argparse
from datetime import datetime, timezone
import json
from pathlib import Path
import sys
import unittest

if __name__ == '__main__':
    parser=argparse.ArgumentParser();parser.add_argument('--report',type=Path,required=True);args=parser.parse_args()
    suite=unittest.defaultTestLoader.discover('tests',pattern='test_*.py')
    result=unittest.TextTestRunner(verbosity=2).run(suite)
    report=dict(format_version=1,completed_utc=datetime.now(timezone.utc).isoformat(),total=result.testsRun,
                failed=len(result.failures),errors=len(result.errors),skipped=len(result.skipped),
                passed=result.testsRun-len(result.failures)-len(result.errors)-len(result.skipped),successful=result.wasSuccessful())
    args.report.parent.mkdir(parents=True,exist_ok=True);args.report.write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8',newline='\n')
    sys.exit(0 if result.wasSuccessful() and report['passed']>0 else 1)
