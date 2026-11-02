import sys,struct,re
def s7(d,i):
    n=0;sh=0
    while True:
        b=d[i];i+=1;n|=(b&0x7f)<<sh;sh+=7
        if b<0x80:break
    return d[i:i+n].decode('utf-8','replace'),i+n
def fields(d,start,end):
    # find "type, assembly" strings followed by field name; read primitive values
    out=[]
    pat=re.compile(rb'(System\.Single|System\.Boolean|System\.Int32), mscorlib[^\x00]*?PublicKeyToken=b77a5c561934e089')
    for m in pat.finditer(d,start,end):
        i=m.end()
        try:
            name,i=s7(d,i)
        except Exception: continue
        t=m.group(1)
        if t==b'System.Single': v=struct.unpack_from('<f',d,i)[0]
        elif t==b'System.Boolean': v=d[i]
        else: v=struct.unpack_from('<i',d,i)[0]
        out.append((name,v))
    return out
for f in sys.argv[1:]:
    d=open(f,'rb').read()
    print('=====',f.split('/')[-1])
    for m in re.finditer(rb'ONetInfo, Assembly-CSharp',d):
        st=m.start()
        nm=d.rfind(b'_Data',0,st); 
        en=d.find(b'm_segments',st)
        print('--- NetInfo at',st)
        fl=fields(d,st,en)
        print(', '.join('%s=%s'%(n,('%.2f'%v if isinstance(v,float) else v)) for n,v in fl))
        # segments / nodes counts
        k=d.find(b'm_segments',st); print('segments raw:',d[k:k+200])
        k2=d.find(b'm_nodes',k); print('nodes raw:',d[k2:k2+40], d[k2+7:k2+11].hex())
