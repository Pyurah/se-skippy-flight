import sys, unicodedata
s = open(sys.argv[1], encoding="utf-8").read()
non = [c for c in s if ord(c) > 127]
cats = {}
for c in non:
    k = unicodedata.category(c)
    cats[k] = cats.get(k, 0) + 1
print("non-ASCII by Unicode category:", cats)
letters = sum(v for k, v in cats.items() if k[0] == "L")
print("letters (valid identifier chars): %d / %d" % (letters, len(non)))

# Confirm NONE of the non-ASCII sit inside a string/char literal.
CODE, STR, VERB, CH = 0, 1, 2, 3
st = CODE; i = 0; n = len(s); in_lit = 0
BS = chr(92)  # backslash
while i < n:
    c = s[i]; nx = s[i + 1] if i + 1 < n else ""
    if st == CODE:
        if c == "@" and nx == '"': st = VERB; i += 2; continue
        if c == '"': st = STR; i += 1; continue
        if c == "'": st = CH; i += 1; continue
    elif st == STR:
        if c == BS:
            if ord(c) > 127: in_lit += 1
            i += 2; continue
        if c == '"': st = CODE
        elif ord(c) > 127: in_lit += 1
    elif st == VERB:
        if c == '"' and nx == '"': i += 2; continue
        if c == '"': st = CODE
        elif ord(c) > 127: in_lit += 1
    elif st == CH:
        if c == BS: i += 2; continue
        if c == "'": st = CODE
        elif ord(c) > 127: in_lit += 1
    i += 1
print("non-ASCII chars INSIDE string/char literals: %d  (expect 0)" % in_lit)
