#!/usr/bin/env python3
"""Read genuine calibration flags from OS 12587811 XDF and P59 BIN.
Does NOT infer SAE PID support or runtime sensor values from calibration flags.
"""
import hashlib, json, sys
from pathlib import Path
import xml.etree.ElementTree as ET
DEFAULT_XDF = Path('/home/bp/Downloads/12587811 OS 2025-07-17/12587811 OS 2025-07-17.xdf')
FLAGS = {'B3801': 'LTFT Correction Enable', 'B4206': 'STFT Open Loop Enable'}
def inspect(bin_path, xdf_path=DEFAULT_XDF):
    raw=Path(bin_path).read_bytes()
    if len(raw)!=1048576: raise ValueError('Expected exactly 1048576 bytes for P59 BIN')
    tree=ET.parse(xdf_path)
    result={'bin':str(bin_path),'sha256':hashlib.sha256(raw).hexdigest(),'bytes':len(raw),'xdf':str(xdf_path),'calibration_flags':{},'sae_mode01_supported_pids':'UNKNOWN: requires firmware diagnostic-handler analysis or real PCM response','simulated_live_values':False}
    for item in tree.getroot().iter('XDFFLAG'):
        name=item.findtext('title','')
        code=name.split(' ',1)[0]
        if code not in FLAGS:continue
        data=item.find('EMBEDDEDDATA')
        mask=int(item.findtext('mask','0'),0)
        offset=int(data.attrib['mmedaddress'],0)
        value=raw[offset]
        result['calibration_flags'][code]={'title':name,'file_offset':f'0x{offset:05X}','mask':f'0x{mask:02X}','byte':f'0x{value:02X}','enabled':bool(value&mask),'source':'OS 12587811 XDF + BIN bytes'}
    if set(result['calibration_flags'])!=set(FLAGS):raise ValueError('Missing expected XDF flags')
    table=next((e for e in tree.getroot().iter('XDFTABLE') if e.findtext('title','').startswith('B4205 -')),None)
    if table is None:raise ValueError('B4205 table missing from XDF')
    axis=next((a for a in table.findall('XDFAXIS') if a.attrib.get('id')=='z'),None)
    data=axis.find('EMBEDDEDDATA')
    offset=int(data.attrib['mmedaddress'],0)
    count=int(data.attrib['mmedrowcount'])
    iat_labels=[int(float(label.attrib['value'])) for label in next(a for a in table.findall('XDFAXIS') if a.attrib.get('id')=='y').findall('LABEL')]
    if count!=len(iat_labels) or data.attrib['mmedelementsizebits']!='16':raise ValueError('Unexpected B4205 shape')
    # This OS 12587811 XDF uses Motorola/big-endian 16-bit calibration values.
    degrees=[int.from_bytes(raw[offset+2*i:offset+2*i+2],'big')*0.0703125+32 for i in range(count)]
    result['closed_loop_temperature_table']={'id':'B4205','address':f'0x{offset:05X}','iat_f':iat_labels,'ect_threshold_f':[round(v,2) for v in degrees],'interpretation':'Calibration eligibility threshold only; actual closed-loop status also depends on PCM runtime conditions'}
    return result
if __name__=='__main__':
    if len(sys.argv)<2: raise SystemExit('usage: inspect_p59_calibration.py BIN [XDF]')
    print(json.dumps(inspect(sys.argv[1],Path(sys.argv[2]) if len(sys.argv)>2 else DEFAULT_XDF),indent=2))
