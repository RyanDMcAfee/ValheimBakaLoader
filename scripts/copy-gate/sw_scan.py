import re,sys,os,glob
root=os.path.abspath(sys.argv[1]) if len(sys.argv)>1 else r"C:/Users/Mithi/OneDrive/Documents/GitHub/ValheimBakaLoader/ValheimBakaLoader"
files=[ "Forms/BlendWindow.Bridge.cs","Game/ValheimServer.cs"]+sorted(glob.glob(os.path.join(root,"Tools","*.cs")))
seen=[]
for f in files:
    p=f if os.path.isabs(f) else os.path.join(root,f)
    rel=os.path.relpath(p,root).replace("\\","/")
    src=open(p,encoding="utf-8-sig").read()
    lines=src.split("\n")
    # crude: walk char by char tracking string / char / comment state
    i=0; n=len(src); line=1
    state=None  # None, 'str', 'vstr', 'char', 'line', 'block'
    buf=""; bufline=0
    while i<n:
        c=src[i]
        if c=="\n": line+=1
        if state is None:
            if src.startswith("//",i): state="line"; i+=2; continue
            if src.startswith("/*",i): state="block"; i+=2; continue
            if src.startswith('@"',i): state="vstr"; buf=""; bufline=line; i+=2; continue
            if src.startswith('$@"',i) or src.startswith('@$"',i): state="vstr"; buf=""; bufline=line; i+=3; continue
            if c=='"': state="str"; buf=""; bufline=line; i+=1; continue
            if c=="'": state="char"; i+=1; continue
            i+=1; continue
        if state=="line":
            if c=="\n": state=None
            i+=1; continue
        if state=="block":
            if src.startswith("*/",i): state=None; i+=2; continue
            i+=1; continue
        if state=="char":
            if c=="\\": i+=2; continue
            if c=="'": state=None
            i+=1; continue
        if state=="str":
            if c=="\\": buf+=src[i:i+2]; i+=2; continue
            if c=='"':
                if " - " in buf: seen.append((rel,bufline,buf))
                state=None; i+=1; continue
            buf+=c; i+=1; continue
        if state=="vstr":
            if src.startswith('""',i): buf+='"'; i+=2; continue
            if c=='"':
                if " - " in buf: seen.append((rel,bufline,buf))
                state=None; i+=1; continue
            buf+=c; i+=1; continue
for rel,ln,b in seen:
    print(f"{rel}:{ln}: {b[:200]}")
print("TOTAL",len(seen))
