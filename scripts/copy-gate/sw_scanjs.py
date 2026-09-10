import sys,io
sys.stdout=io.TextIOWrapper(sys.stdout.buffer,encoding='utf-8',errors='replace')
p=sys.argv[1]
src=open(p,encoding="utf-8-sig").read()
n=len(src); i=0; line=1; state=None; buf=""; bufline=0; out=[]
prev=""   # last significant char seen in code
REGEX_PREV=set("(,=:[!&|?{};+-*%~^<>\n")
while i<n:
    c=src[i]
    if state is None:
        if src.startswith("//",i): state="line"; i+=2; continue
        if src.startswith("/*",i): state="block"; i+=2; continue
        if c=="/" and (prev=="" or prev in REGEX_PREV):
            state="re"; i+=1; continue
        if c in "\"'`": state=c; buf=""; bufline=line; i+=1; continue
        if c=="\n": line+=1
        if not c.isspace(): prev=c
        i+=1; continue
    if state=="line":
        if c=="\n": state=None; line+=1
        i+=1; continue
    if state=="block":
        if c=="\n": line+=1
        if src.startswith("*/",i): state=None; i+=2; continue
        i+=1; continue
    if state=="re":
        if c=="\\": i+=2; continue
        if c=="[":
            i+=1
            while i<n and src[i]!="]":
                if src[i]=="\\": i+=1
                i+=1
            i+=1; continue
        if c=="/": state=None; prev="/"; i+=1; continue
        if c=="\n": state=None; line+=1  # not a regex after all
        i+=1; continue
    # string states
    if c=="\\":
        buf+=src[i:i+2]
        if src[i+1:i+2]=="\n": line+=1
        i+=2; continue
    if c=="\n": line+=1
    if c==state:
        if " - " in buf: out.append((bufline,buf))
        state=None; prev='"'; i+=1; continue
    buf+=c; i+=1; continue
for ln,b in out: print(f"{ln}: {b[:300]}")
print("TOTAL",len(out))
