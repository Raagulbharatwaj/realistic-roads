import struct,sys
def parse(path):
    d=open(path,'rb').read()
    i=d.find(b'm_paths')+7
    n=struct.unpack_from('<i',d,i)[0]; i+=4
    out=[]
    def i32():
        nonlocal i
        v=struct.unpack_from('<i',d,i)[0]; i+=4; return v
    for k in range(n):
        L=d[i]; i+=1
        name=d[i:i+L].decode('utf-8','replace'); i+=L
        def v3s():
            nonlocal i
            c=i32(); r=[struct.unpack_from('<3f',d,i+12*j) for j in range(c)]; i+=12*c; return r
        nodes=v3s(); curves=v3s()
        inv=d[i]; i+=1
        snap=struct.unpack_from('<f',d,i)[0]; i+=4
        c=i32(); forbid=list(d[i:i+c]); i+=c
        c=i32(); tl=[struct.unpack_from('<i',d,i+4*j)[0] for j in range(c)]; i+=4*c
        c=i32(); yld=list(d[i:i+c]); i+=c
        extra=d[i:i+2].hex(); i+=2
        out.append(dict(name=name,nodes=nodes,curves=curves,inv=inv,snap=snap,forbid=forbid,tl=tl,yld=yld,extra=extra))
    return out
if __name__=='__main__':
  for f in sys.argv[1:]:
    print('=====',f.split('/')[-1])
    for k,p in enumerate(parse(f)):
        fmt=lambda vs:' '.join('(%.1f,%.1f,%.1f)'%v for v in vs)
        print('%2d %-34s %s | curve %s | inv%d forbid%s TL%s yld%s x%s'%(k,p['name'].replace('2182179214.','').replace('2182185994.',''),fmt(p['nodes']),fmt(p['curves']),p['inv'],p['forbid'],p['tl'],p['yld'],p['extra']))
