import json,sys,subprocess
sys.path.insert(0,sys.argv[1])
from paths import parse as parse_paths
from nrbf import parse as nrbf_parse, resolve, Ref
f=sys.argv[2]
paths=parse_paths(f)
d=open(f,'rb').read()
objs,_=nrbf_parse(d,d.find(b'TMPE_Records')+len('TMPE_Records')+4)
j=resolve(objs,Ref(1))
ids=j['PathNetworkIDs']
assert len(ids)==len(paths),(len(ids),len(paths))
TB={0:'-',1:'no',2:'yes'}
pos={}   # node id -> position
segs={}
for p,s in zip(paths,ids):
    pos[s['StartNodeId']]=p['nodes'][0]; pos[s['EndNodeId']]=p['nodes'][-1]
    segs[s['SegmentId']]=(p,s)
lane_owner={}
for sid,(p,s) in segs.items():
    for i,l in enumerate(s['laneIDs']): lane_owner[l]=(sid,i)
nodes={};ends={}
def walk(o):
    if isinstance(o,dict):
        c=o.get('__class','')
        if c.endswith('.NodeRecord'): nodes[o['<NodeId>k__BackingField']]=o
        if c.endswith('SegmentEndRecord'): ends[(o['<SegmentId>k__BackingField'],o['<StartNode>k__BackingField'])]=o
        for v in o.values(): walk(v)
    elif isinstance(o,list):
        for v in o: walk(v)
walk(j)
short=lambda n:n.replace('2182179214.','').replace('2182185994.','').replace('2200481960.','').replace('2376923126.','').replace('_Data','')
def name(sid): return short(segs[sid][0]['name'])
# nodes where a raised piece (ramp/slope) meets ground pieces
for nid,p in sorted(pos.items(), key=lambda x:(x[1][0],x[1][2])):
    at=[(sid,s['StartNodeId']==nid) for sid,(pp,s) in segs.items() if nid in (s['StartNodeId'],s['EndNodeId'])]
    names=[name(sid) for sid,_ in at]
    if not any(('Elevated' in n or 'Slope' in n or 'E0' in n) for n in names) or abs(p[1])>0.5: continue
    print('NODE %d at (%.0f,%.0f,%.0f): %d segments'%(nid,p[0],p[1],p[2],len(at)))
    nr=nodes.get(nid,{})
    print('   TM:PE traffic light:',nr.get('trafficLight_'))
    for sid,start in at:
        e=ends.get((sid,start),{})
        g=lambda k:TB.get((e.get(k) or {}).get('value__'),'?')
        print('   seg %-5d %-30s %s end | crossing %s, u-turn %s, lane change %s, blocked junction %s'%(sid,name(sid),'start' if start else 'end',g('pedestrianCrossingAllowed_'),g('uturnAllowed_'),g('laneChangingAllowedWhenGoingStraight_'),g('enteringBlockedJunctionAllowed_')))
    for l in [x for x in ((nr.get('lanes_') or {}).get('_items') or []) if x]:
        src=lane_owner.get(l['LaneId'])
        tgts=[lane_owner.get(t) for t in (l.get('roadConnections_') or [])]
        if not tgts: continue
        print('     lane %s/%d -> %s'%(name(src[0]) if src else '?',src[1] if src else -1,', '.join('%s/%d'%(name(t[0]),t[1]) for t in tgts if t)))
