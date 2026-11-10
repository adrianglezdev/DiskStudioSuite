# Extrae el logo de Disk Studio Suite (JPG con fondo de cuadros) a PNG transparente + icono .ico.
# Uso (desde la raíz del repositorio):  python docs/marca/extraer-logo.py
# Requiere: pip install pillow numpy
from PIL import Image, ImageFilter
import numpy as np, os

here = os.path.dirname(os.path.abspath(__file__))
src = os.path.join(here, "logo-original.jpg")
out = os.path.join(here, "..", "..", "src", "Assets")
scratch = here
im = np.asarray(Image.open(src).convert("RGB")).astype(np.float32)
H, W, _ = im.shape
chroma = im.max(2) - im.min(2)
lum = im @ np.array([0.299, 0.587, 0.114], np.float32)

def dilate(m, n):
    for _ in range(n):
        g = m.copy(); g[1:] |= m[:-1]; g[:-1] |= m[1:]; g[:, 1:] |= m[:, :-1]; g[:, :-1] |= m[:, 1:]; m = g
    return m
def erode(m, n): return ~dilate(~m, n)

# ── 1. Modelo del fondo de cuadros ──
def grid(profile):
    e = np.where(np.abs(np.diff((profile > 58).astype(int))) > 0)[0]
    return (e[-1] - e[0]) / (len(e) - 1), e[0] + 1.0
px, ox = grid(lum[3, :])
py_, oy = grid(lum[:, 3])
per = (px + py_) / 2
yy, xx = np.mgrid[0:H, 0:W]
sq = (np.floor((xx - ox) / per) + np.floor((yy - oy) / per)).astype(int) % 2
light = sq == 0          # la primera casilla tras el borde inicial (x=0..ox) es clara
period = per
cb = np.asarray(Image.fromarray(np.clip(chroma * 8, 0, 255).astype(np.uint8)).filter(ImageFilter.BoxBlur(3))).astype(np.float32) / 8
bgsure = (cb < 4) & (chroma < 6)
dark_col = np.median(im[bgsure & ~light], 0); light_col = np.median(im[bgsure & light], 0)
B = np.where(light[..., None], light_col, dark_col).astype(np.float32)
print("cuadro", period, "px · oscuro", dark_col, "claro", light_col)

# ── 2. Fondo conectado con el borde (rellena huecos del cubo) ──
fg = (cb > 6.5) | (lum > 125)
cand = ~fg
reach = np.zeros_like(cand); reach[0] = cand[0]; reach[-1] = cand[-1]; reach[:, 0] = cand[:, 0]; reach[:, -1] = cand[:, -1]
while True:
    g = dilate(reach, 1) & cand
    if (g == reach).all(): break
    reach = g
solid = ~reach                       # logo (posiblemente con 3 px de más por el suavizado)

# ── 3. Colores puros del interior y propagación al borde ──
diff = np.linalg.norm(im - B, axis=2)
core = erode(solid, 3) | (solid & (chroma > 60) & (diff > 25))
F = np.where(core[..., None], im, 0).astype(np.float32)
known = core.copy()
for _ in range(10):
    acc = np.zeros_like(F); cnt = np.zeros((H, W), np.float32)
    for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (-1, -1), (1, -1), (-1, 1)):
        sF = np.roll(F, (dy, dx), (0, 1)); sk = np.roll(known, (dy, dx), (0, 1))
        acc += sF * sk[..., None]; cnt += sk
    new = (~known) & (cnt > 0)
    F[new] = acc[new] / cnt[new][:, None]
    known |= new

# ── 4. Alfa por desmezcla: C = a·F + (1−a)·B ──
d = F - B
den = (d * d).sum(2) + 1e-3
a = np.clip(((im - B) * d).sum(2) / den, 0, 1)
# Donde el color del logo se parece demasiado al cuadro de detrás, la desmezcla no es fiable: borde nítido
ambiguous = den < 40 * 40
a = np.where(ambiguous, np.where(erode(solid, 2), 1.0, 0.0), a)
a = np.where(core, 1.0, a)
a = np.where(solid & known, a, 0.0)
a = np.where(dilate(reach, 0) & ~dilate(solid, 0), 0.0, a)
a[a < 0.12] = 0
# Suavizado de color del interior (ruido JPG) sin tocar bordes
Fs = np.asarray(Image.fromarray(np.clip(F, 0, 255).astype(np.uint8)).filter(ImageFilter.MedianFilter(5))).astype(np.float32)
Fs = np.where(core[..., None] & erode(core, 2)[..., None], Fs, F)
alpha8 = Image.fromarray((a * 255).astype(np.uint8))
alpha8 = alpha8.filter(ImageFilter.MinFilter(3)).filter(ImageFilter.GaussianBlur(0.6))   # quita el anillo exterior
rgba = np.dstack([np.clip(Fs, 0, 255), np.asarray(alpha8)]).astype(np.uint8)
img = Image.fromarray(rgba, "RGBA")

bbox = img.getchannel("A").point(lambda v: 255 if v > 60 else 0).getbbox()
img = img.crop(bbox)
w, h = img.size
side = int(max(w, h) * 1.04)
sqi = Image.new("RGBA", (side, side), (0, 0, 0, 0))
sqi.paste(img, ((side - w) // 2, (side - h) // 2))
sqi = sqi.resize((1024, 1024), Image.LANCZOS)
sqi.save(os.path.join(out, "logo.png"))
sqi.resize((512, 512), Image.LANCZOS).save(os.path.join(out, "logo-512.png"))
sqi.save(os.path.join(out, "app.ico"), sizes=[(s, s) for s in [16, 20, 24, 32, 40, 48, 64, 128, 256]])
for name, col in (("vista-oscura.png", (13, 31, 27)), ("vista-clara.png", (245, 247, 246))):
    bg = Image.new("RGBA", (1100, 560), col + (255,))
    bg.alpha_composite(sqi.resize((520, 520), Image.LANCZOS), (20, 20))
    x = 600
    for s in [16, 24, 32, 48, 64, 128]:
        bg.alpha_composite(sqi.resize((s, s), Image.LANCZOS), (x, 240)); x += s + 20
    bg.convert("RGB").save(os.path.join(scratch, name))
print("ok")
