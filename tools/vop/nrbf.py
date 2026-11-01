import struct,sys,json
class R:
    def __init__(s,d,i): s.d=d; s.i=i
    def u8(s): v=s.d[s.i]; s.i+=1; return v
    def i32(s): v=struct.unpack_from('<i',s.d,s.i)[0]; s.i+=4; return v
    def fmt(s,f): v=struct.unpack_from(f,s.d,s.i)[0]; s.i+=struct.calcsize(f); return v
    def s7(s):
        n=0;sh=0
        while True:
            b=s.u8(); n|=(b&0x7f)<<sh; sh+=7
            if b<0x80: break
        v=s.d[s.i:s.i+n].decode('utf-8','replace'); s.i+=n; return v
PF={1:'<?',2:'<B',3:'<B',6:'<d',7:'<h',8:'<i',9:'<q',10:'<b',11:'<f',12:'<q',13:'<q',14:'<H',15:'<I',16:'<Q'}
class Ref:
    def __init__(s,i): s.id=i
    def __repr__(s): return '#%d'%s.id
def parse(d,i):
    r=R(d,i); objs={}; classes={}; root=None
    def prim(t):
        if t==18: return r.s7()
        if t==5: return r.s7()
        return r.fmt(PF[t])
    def read_value(bt,extra):
        if bt==0: return prim(extra)
        return record()
    def class_body(oid,name,members,types,extras):
        o={'__class':name}
        objs[oid]=o
        for m,bt,ex in zip(members,types,extras):
            v=read_value(bt,ex)
            if isinstance(v,tuple) and v[0]=='nulls':
                v=None
            o[m]=v
        return Ref(oid)
    def member_info(n,with_types):
        members=[r.s7() for _ in range(n)]
        if not with_types: return members,[2]*n,[None]*n
        types=[r.u8() for _ in range(n)]
        extras=[]
        for t in types:
            if t in (0,7): extras.append(r.u8())
            elif t==3: extras.append(r.s7())
            elif t==4: extras.append((r.s7(),r.i32()))
            else: extras.append(None)
        return members,types,extras
    pending_nulls=[0]
    def record():
        rt=r.u8()
        if rt==0: r.i+=16; return ('hdr',)
        if rt==12: r.i32(); r.s7(); return record()
        if rt in (4,5):
            oid=r.i32(); name=r.s7(); n=r.i32()
            m,t,e=member_info(n,True)
            if rt==5: r.i32()
            classes[oid]=(name,m,t,e)
            return class_body(oid,name,m,t,e)
        if rt in (2,3):
            oid=r.i32(); name=r.s7(); n=r.i32()
            m,t,e=member_info(n,False)
            if rt==3: r.i32()
            classes[oid]=(name,m,t,e); return class_body(oid,name,m,t,e)
        if rt==1:
            oid=r.i32(); meta=r.i32(); name,m,t,e=classes[meta]
            classes[oid]=classes[meta]
            return class_body(oid,name,m,t,e)
        if rt==6:
            oid=r.i32(); v=r.s7(); objs[oid]=v; return v
        if rt==8:
            t=r.u8(); return prim(t)
        if rt==9: return Ref(r.i32())
        if rt==10: return None
        if rt==13: return ('nulls',r.u8())
        if rt==14: return ('nulls',r.i32())
        if rt==15:
            oid=r.i32(); n=r.i32(); t=r.u8()
            v=[prim(t) for _ in range(n)]; objs[oid]=v; return Ref(oid)
        if rt in (16,17):
            oid=r.i32(); n=r.i32(); v=[]
            while len(v)<n:
                x=record()
                if isinstance(x,tuple) and x[0]=='nulls': v+= [None]*x[1]
                else: v.append(x)
            objs[oid]=v; return Ref(oid)
        if rt==7:
            oid=r.i32(); at=r.u8(); rank=r.i32(); lens=[r.i32() for _ in range(rank)]
            if at in (3,4,5): [r.i32() for _ in range(rank)]
            bt=r.u8(); ex=None
            if bt in (0,7): ex=r.u8()
            elif bt==3: ex=r.s7()
            elif bt==4: ex=(r.s7(),r.i32())
            n=1
            for l in lens: n*=l
            v=[]
            while len(v)<n:
                x=read_value(bt,ex)
                if isinstance(x,tuple) and x[0]=='nulls': v+=[None]*x[1]
                else: v.append(x)
            objs[oid]=v; return Ref(oid)
        if rt==11: return ('end',)
        raise Exception('record type %d at %d'%(rt,r.i-1))
    out=[]
    while True:
        x=record()
        if isinstance(x,tuple) and x[0]=='end': break
        out.append(x)
    return objs,out
def resolve(objs,v,depth=0,seen=None):
    seen=seen or set()
    if isinstance(v,Ref):
        if v.id in seen or depth>40: return '#%d'%v.id
        return resolve(objs,objs.get(v.id),depth+1,seen|{v.id})
    if isinstance(v,dict): return {k:resolve(objs,x,depth+1,seen) for k,x in v.items()}
    if isinstance(v,list): return [resolve(objs,x,depth+1,seen) for x in v]
    return v
if __name__=='__main__':
    d=open(sys.argv[1],'rb').read()
    i=d.find(b'TMPE_Records')+len('TMPE_Records')+4
    objs,out=parse(d,i)
    root=resolve(objs,Ref(1))
    print(json.dumps(root,indent=1,default=str))
