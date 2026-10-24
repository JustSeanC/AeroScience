"""Reproduce synthetic 1C files using the unchanged 1B NetCDF-C generator.
No network. Output filenames use a past date for normal Library date validation,
while test_origin and this receipt explicitly identify synthetic measurements.
Refuses overwrites; run in a disposable checkout or a new --output directory.
"""
from pathlib import Path
import argparse
import sys
p=argparse.ArgumentParser();p.add_argument('--output',type=Path,required=True);a=p.parse_args()
source=(Path(__file__).resolve().parents[1]/'AeroScience.EmitFixtures/generate_fixtures.py').read_text()
for original,synthetic in [('20990101T000000','20220810T034103'),('emit20990101t000000','emit20220810t034103'),('2099-01-01T00:00:00','2022-08-10T03:41:03'),('2099-01-01T00:00:01','2022-08-10T03:41:04'),('2099-01-01T00:00:12','2022-08-10T03:41:15'),('2099-01-01 intentionally synthetic','2022-08-10 synthetic contract fixture; NOT genuine NASA')]:source=source.replace(original,synthetic)
sys.argv=['generate_fixtures.py','--only','standard','--output',str(a.output)]
exec(compile(source,str(__file__),'exec'))
