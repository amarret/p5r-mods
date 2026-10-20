"""Regenerates Configuration/Config.cs from the game's INIT/CMM.BIN.

Usage:  python tools/gen_config.py <path to extracted INIT/CMM.BIN> Configuration/Config.cs

CMM.BIN lives in EN.CPK (extract it with CriFsLib.GUI or any CPK tool). The script reads
cmmFunctionTable.ctd (which confidant/rank grants which ability ID), cmmFunctionName.ctd
(names), cmmFunctionInfoTable.ctd and cmmFunctionHelp.bmd (descriptions).
"""
import re, struct, sys

CMM_BIN, OUT = sys.argv[1], sys.argv[2]


def unpack_pak(data):
    """Big-endian Atlus PAK: u32 count, then {char name[32]; u32 size; bytes}."""
    files, off = {}, 4
    for _ in range(struct.unpack_from('>I', data, 0)[0]):
        name = data[off:off + 32].split(b'\0')[0].decode()
        size = struct.unpack_from('>I', data, off + 32)[0]
        files[name] = data[off + 36:off + 36 + size]
        off += 36 + size
    return files


def ctd_table(data):
    """FTD0 container, first sub-table: returns (element count, element bytes)."""
    assert data[4:8] == b'FTD0'
    o = struct.unpack_from('>I', data, 16)[0]
    size, cnt = struct.unpack_from('>II', data, o + 4)
    return cnt, data[o + 16:o + 16 + size]


def ctd_strings(data):
    cnt, body = ctd_table(data)
    es = len(body) // cnt
    return [body[i * es:(i + 1) * es].split(b'\0')[0].decode('utf-8', 'replace') for i in range(cnt)]


def bmd_texts(data):
    """Big-endian MSG1: message table at 0x20 of {u32 type, u32 offset}; 24-byte name, then text."""
    cnt = struct.unpack_from('>I', data, 0x18)[0]
    offs = [struct.unpack_from('>II', data, 0x20 + i * 8)[1] + 0x20 for i in range(cnt)] + [len(data)]
    out = []
    for i in range(cnt):
        blob = data[offs[i] + 24:offs[i + 1]]
        out.append(b' '.join(re.findall(rb'[ -~]{2,}', blob.replace(b'\n', b' '))).decode().strip())
    return out


cmm = unpack_pak(open(CMM_BIN, 'rb').read())
fn = ctd_strings(cmm['cmmFunctionName.ctd'])
icnt, idata = ctd_table(cmm['cmmFunctionInfoTable.ctd'])
info = [struct.unpack_from('>HHI', idata, i * 8) for i in range(icnt)]
helps = bmd_texts(cmm['cmmFunctionHelp.bmd'])
_, tdata = ctd_table(cmm['cmmFunctionTable.ctd'])


def table_row(cmm_id):
    """Ten 12-byte slots per confidant: {u16 flags, s16 rank, u16 ability ID, u16, u32 required flag}."""
    row = tdata[cmm_id * 0x78:(cmm_id + 1) * 0x78]
    out = []
    for s in range(10):
        _, rank, fid, _, _ = struct.unpack_from('>HhHHI', row, s * 12)
        if rank >= 0:
            out.append((rank, fid))
    return out


def clean_help(t):
    if not re.match(r'^A[a-z]', t):
        t = re.sub(r'^.{0,6}?A(?=[A-Z][a-z])', '', t)
    return t.split('  ')[0].strip()

# Display names that the game stores with its own glyph encoding.
NAME_FIX = {254: "Chaînés Hook", 281: "Sexy Technique"}
# Extra wording for the config label only (the property name stays based on NAME_FIX / the game name).
LABEL_SUFFIX = {254: " (Joker's grappling-hook attack)"}
HELP_FIX = {
    2: "See things normally invisible to the naked eye (Third Eye button).",
    254: "Allows Joker to ambush enemies from a distance by attacking them with the grappling hook. "
         "Off = the hook can no longer be used to attack enemies; grappling onto ledges and grapple points works as usual.",
}
# Older ability IDs carried over from vanilla P5 that some scripts still query.
LEGACY_IDS = {281: [66], 283: [270], 274: [44], 275: [48], 240: [81], 243: [84],
              280: [151], 282: [153], 286: [155], 289: [157], 276: [121], 278: [127], 110: [109]}
WARN = {
    1: " WARNING: disabling it means you cannot negotiate after a Hold Up (the forced story tutorial may misbehave).",
    2: " WARNING: some Palace puzzles and story moments expect Third Eye to work.",
    4: " Not verified in-game: keep your Persona stock within the smaller size before disabling.",
    5: " Not verified in-game: keep your Persona stock within the smaller size before disabling.",
    8: " Not verified in-game: keep your Persona stock within the smaller size before disabling.",
    113: " Disabling hides the Group Guillotine fusion option.",
    141: " Disabling also affects the scheduler's class activities with Kawakami.",
}
# Second Awakening(s) are one-off Persona evolutions, not a toggleable effect.
EXCLUDE = {231, 232, 233, 234, 236, 237, 239, 241, 242, 244, 245, 246, 249, 256, 259, 265, 268}

GROUPS = [
    ("Fool – Igor", "Fool", [1]),
    ("Magician – Morgana", "Morgana", [2]),
    ("Priestess – Makoto", "Makoto", [3]),
    ("Empress – Haru", "Haru", [4]),
    ("Emperor – Yusuke", "Yusuke", [5]),
    ("Hierophant – Sojiro", "Sojiro", [6]),
    ("Lovers – Ann", "Ann", [7]),
    ("Chariot – Ryuji", "Ryuji", [8]),
    ("Justice – Akechi", "Akechi", [9]),
    ("Hermit – Futaba", "Futaba", [10]),
    ("Fortune – Chihaya", "Chihaya", [11]),
    ("Strength – Caroline & Justine", "Twins", [12]),
    ("Hanged Man – Iwai", "Iwai", [13]),
    ("Death – Takemi", "Takemi", [14]),
    ("Temperance – Kawakami", "Kawakami", [15]),
    ("Devil – Ohya", "Ohya", [16]),
    ("Tower – Shinya", "Shinya", [17]),
    ("Star – Hifumi", "Hifumi", [18]),
    ("Moon – Mishima", "Mishima", [19]),
    ("Sun – Yoshida", "Yoshida", [20]),
    ("Faith – Kasumi / Sumire", "Faith", [36]),
    ("Councillor – Maruki", "Maruki", [35]),
]

# Switches that are not CMM abilities, appended to a confidant's section (keyed by prefix).
EXTRA = {
    "Futaba": [
        ("Futaba_AllOutAttackTakeover", "Futaba's All-Out Attack takeover",
         "On = vanilla: when the party starts an All-Out Attack, Futaba can take it over with her own bomb "
         "animation (Explosive Scheme). Off = she never takes over; the normal All-Out Attack always plays."),
    ],
}

SHOWTIMES = [
    ("MorganaAnn", "Morgana & Ann"),
    ("MorganaHaru", "Morgana & Haru"),
    ("AnnYusuke", "Ann & Yusuke"),
    ("RyujiYusuke", "Ryuji & Yusuke"),
    ("RyujiMakoto", "Ryuji & Makoto"),
    ("JokerAkechi", "Joker & Akechi"),
    ("MakotoHaru", "Makoto & Haru"),
    ("JokerKasumi", "Joker & Kasumi"),
]

def pascal(name):
    name = name.replace('î', 'i').replace('é', 'e').replace("'", '')
    return ''.join(w[:1].upper() + w[1:] for w in re.split(r'[^A-Za-z0-9]+', name) if w)

def cs(s):
    return s.replace('\\', '\\\\').replace('"', '\\"')

lines = []
emit = lines.append
order = 0
abilities = []  # (prop, [ids])

emit('// <auto-generated>')
emit('// Generated from the game\'s INIT/CMM.BIN (cmmFunctionTable / cmmFunctionName / cmmFunctionHelp).')
emit('// </auto-generated>')
emit('using System.ComponentModel;')
emit('using System.ComponentModel.DataAnnotations;')
emit('using p5rpc.confidanttoggles.Template.Configuration;')
emit('')
emit('namespace p5rpc.confidanttoggles.Configuration;')
emit('')
emit('public class Config : Configurable<Config>')
emit('{')

def prop(category, prop_name, display, desc):
    global order
    emit(f'    [Category("{cs(category)}")]')
    emit(f'    [DisplayName("{cs(display)}")]')
    emit(f'    [Description("{cs(desc)}")]')
    emit('    [DefaultValue(true)]')
    emit(f'    [Display(Order = {order})]')
    emit(f'    public bool {prop_name} {{ get; set; }} = true;')
    emit('')
    order += 1

cat = "01. Showtimes"
for key, label in SHOWTIMES:
    prop(cat, f"Showtime_{key}", label,
         f"On = the {label} Showtime can trigger once unlocked. "
         "Off = it never triggers, even if it is (or later becomes) unlocked. Your unlock progress is kept.")

used_props = set()
for gi, (label, prefix, rows) in enumerate(GROUPS):
    cat = f"{gi + 2:02d}. {label}"
    seen = set()
    entries = []
    for cmm in rows:
        for rank, fid in table_row(cmm):
            if fid in EXCLUDE or fid in seen:
                continue
            seen.add(fid)
            entries.append((rank, fid))
    entries.sort(key=lambda e: (e[0], e[1]))
    for rank, fid in entries:
        name = NAME_FIX.get(fid, fn[fid])
        desc = HELP_FIX.get(fid) or clean_help(helps[info[fid][1]])
        desc = desc + WARN.get(fid, "")
        p = f"{prefix}_{pascal(name)}"
        if p in used_props:
            p = f"{p}_{fid}"
        used_props.add(p)
        prop(cat, p, f"Rank {rank}: {name}{LABEL_SUFFIX.get(fid, '')}", desc + f" (ability ID {fid})")
        abilities.append((p, [fid] + LEGACY_IDS.get(fid, [])))
    for p, display, desc in EXTRA.get(prefix, []):
        prop(cat, p, display, desc)

emit('    /// <summary>Ability IDs (CMM function IDs) the player has switched off.</summary>')
emit('    public IEnumerable<int> GetDisabledAbilityIds()')
emit('    {')
for p, ids in abilities:
    for i in ids:
        emit(f'        if (!{p}) yield return {i};')
emit('    }')
emit('')
emit('    /// <summary>Showtime indices (0-7, game table order) the player has switched off.</summary>')
emit('    public IEnumerable<int> GetDisabledShowtimeIndices()')
emit('    {')
for i, (key, _) in enumerate(SHOWTIMES):
    emit(f'        if (!Showtime_{key}) yield return {i};')
emit('    }')
emit('}')
emit('')
emit('/// <summary>')
emit('/// Allows you to override certain aspects of the configuration creation process (e.g. create multiple configurations).')
emit('/// Override elements in <see cref="ConfiguratorMixinBase"/> for finer control.')
emit('/// </summary>')
emit('public class ConfiguratorMixin : ConfiguratorMixinBase')
emit('{')
emit('}')

with open(OUT, 'w', encoding='utf-8', newline='\r\n') as f:
    f.write('\n'.join(lines) + '\n')
print(f'{len(abilities)} abilities, {len(SHOWTIMES)} showtimes -> {OUT}')
