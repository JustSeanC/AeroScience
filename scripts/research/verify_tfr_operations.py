"""Independently compare recorded public FAA XML/AIXM, PROJ geometry and actual application projections.

Requires developer-only rasterio and Shapely. Does not establish current activation,
legal applicability, complete coverage, or installed-device acceptance.
"""
import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import xml.etree.ElementTree as ET

A = '{http://www.aixm.aero/schema/5.0}'
G = '{http://www.opengis.net/gml/3.2}'
T = '{http://www.faa.gov/AIM/SAA/TFR}'
X = '{http://www.w3.org/1999/xlink}'


def verify_originals(folder):
    receipts = json.loads((folder/'receipts.json').read_text())
    assert len(receipts) == 11
    for record in receipts:
        raw = (folder/(record['name']+'.original')).read_bytes()
        assert record['status'] == 200 and len(raw) == record['bytes']
        assert hashlib.sha256(raw).hexdigest() == record['sha256'], 'Pinned FAA original changed.'
        assert record['url'].startswith('https://tfr.faa.gov/')
    return receipts


def verify(folder, application):
    from rasterio.warp import transform
    from shapely.geometry import Polygon, shape
    from shapely.ops import transform as project_geometry
    receipts = verify_originals(folder)
    receipt = json.loads((application/'operations-receipt.json').read_text())
    assert receipt['offlineCalls'] == 0 and receipt['httpCalls'] == 11
    original = json.loads((folder/'geometry.original').read_bytes())
    by_gid = {f['properties']['GID']: f for f in original['features']}
    areas = windows = coordinates = 0
    maximum = 0.0
    for view in receipt['views']:
        id = view['notamId']
        prefix = 'notice' if id == '6/2070' else None
        xml_path = folder/((prefix+'-xml' if prefix else 'xml-'+id.replace('/','_'))+'.original')
        aixm_path = folder/((prefix+'-aixm' if prefix else 'aixm-'+id.replace('/','_'))+'.original')
        assert hashlib.sha256(xml_path.read_bytes()).hexdigest() == view['xmlSha256']
        assert hashlib.sha256(aixm_path.read_bytes()).hexdigest() == view['aixmSha256']
        xml, aixm = ET.fromstring(xml_path.read_bytes()), ET.fromstring(aixm_path.read_bytes())
        assert str(int(xml.findtext('.//dateIndexYear')) % 10)+'/'+xml.findtext('.//noSeqNo') == id == aixm.findtext('.//'+T+'number')
        assert 'Unknown' in view['lifecycleStatus']
        for result in view['areas']:
            areas += 1
            group = next(g for g in xml.findall('.//TFRAreaGroup') if g.findtext('aseTFRArea/AseUid/codeId') == result['id'])
            area = group.find('aseTFRArea')
            for key, suffix in [('lower','Lower'), ('upper','Upper')]:
                assert result[key]['value'] == float(area.findtext('valDistVer'+suffix))
                assert result[key]['unit'] == area.findtext('uomDistVer'+suffix)
                assert result[key]['reference'] == {'HEI':'AGL','ALT':'MSL','STD':'Standard pressure'}[area.findtext('codeDistVer'+suffix)]
            if result['windows']:
                assert area.findtext('isScheduledTfrArea') == 'FALSE'
                airspace = next(e for e in aixm.findall('.//'+A+'Airspace') if e.findtext(A+'timeSlice/'+A+'AirspaceTimeSlice/'+A+'name') == result['name'].upper())
                usage = next(e for e in aixm.findall('.//'+A+'AirspaceUsageTimeSlice') if e.find(A+'restrictedAirspace').get(X+'href') == '#'+airspace.get(G+'id'))
                sheet = usage.find('.//'+A+'Timesheet')
                assert sheet.findtext(A+'timeReference') == 'UTC'
                assert len(result['windows']) == 1
                w = result['windows'][0]
                for key, date_field, prefix in [('startUtc','dateEffective','start'),('endUtc','dateExpire','end')]:
                    date = datetime.fromisoformat(area.findtext('ScheduleGroup/'+date_field)).replace(tzinfo=timezone.utc)
                    assert date == datetime.fromisoformat(w[key])
                    assert date.strftime('%d-%m') == sheet.findtext(A+prefix+'Date')
                    assert date.strftime('%H:%M') == sheet.findtext(A+prefix+'Time')
                windows += 1
            else:
                assert 'Unknown' in result['scheduleStatus']
            # Independent NASA-free FAA merged-ring vs PROJ-transformed WFS join.
            def coordinate(value):
                return float(value[:-1]) * (-1 if value[-1] in 'SW' else 1)
            ring = [(coordinate(v.findtext('geoLong')),coordinate(v.findtext('geoLat'))) for v in group.findall('abdMergedArea/Avx')]
            polygon = Polygon(ring)
            assert polygon.is_valid
            boundary = next(b for b in receipt['boundaries'] if b['notamId'] == id and b.get('operational',{}).get('areas',[{}])[0].get('id') == result['id'])
            wfs = shape(by_gid[boundary['sourceGeometryId']]['geometry'])
            geo = project_geometry(lambda x,y: transform('EPSG:3857','EPSG:4326',x,y),wfs)
            difference = polygon.hausdorff_distance(geo)
            assert difference <= 1e-8, 'WFS/merged XML representation join exceeds decimal-rounding bound.'
            maximum = max(maximum,difference)
            coordinates += len(ring)*2
    assert (areas,windows) == (5,3)
    assert len(receipt['screens']) == 5
    assert all(s['snapshotComplete'] is False and s['outcome'] in ['Potential intersection','Unable to determine'] for s in receipt['screens'])
    return {'version':1,'evidenceClass':'Genuine public FAA originals + independent XML/AIXM/PROJ/Shapely checks + real application/cache/offline replay; not current activation or device acceptance',
        'sourceReceipts':receipts,'applicationReceiptSha256':hashlib.sha256((application/'operations-receipt.json').read_bytes()).hexdigest(),
        'applicationClockUtc':receipt['applicationClockUtc'],'notices':4,'areasCompared':areas,'crossCheckedUtcWindows':windows,'recurringSchedulesExplicitlyUnknown':2,
        'coordinateComponentsCompared':coordinates,'maximumMergedVsWfsDifferenceDegrees':maximum,'offlineNetworkCalls':receipt['offlineCalls'],
        'limitations':['No complete amendment/cancellation feed or authenticated NOTAM API is verified.','Recurring schedules, legal aircraft applicability, missing shapes, operational completeness and stale/offline current activation remain unverified.','Installed Windows/MSFS/iPad/Quest acceptance and EMIT genuine-original acceptance are separate and pending.']}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--originals',type=Path,required=True)
    parser.add_argument('--application',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    args=parser.parse_args()
    args.output.write_text(json.dumps(verify(args.originals,args.application),indent=2)+'\n')
    print('PASS: 4 genuine FAA notices, 5 area/altitude/geometry joins, 3 independently cross-checked UTC windows; 2 recurring schedules remain Unknown.')
