"""Derive smooth layout masks from the reference map (coastline, lake+river, roads, sand, cliffs, paved yard).
Writes 512x512 grayscale PNGs into Assets/LevelData. Image pixel (px,py) maps to world
x = (px-627)*0.344, z = (627-py)*0.344 (one 6x6 grid cell = 67 m, image = 431 m).
Usage: python Tools/extract_map_masks.py <map.png> <out_dir> [debug_dir]"""
import sys, os
import numpy as np
from PIL import Image
import cv2
from scipy import ndimage as ndi

src, out = sys.argv[1], sys.argv[2]
dbg = sys.argv[3] if len(sys.argv) > 3 else None
os.makedirs(out, exist_ok=True)
im = np.array(Image.open(src).convert("RGB")).astype(np.float32)
H, W = im.shape[:2]
r, g, b = im[..., 0], im[..., 1], im[..., 2]
hsv = cv2.cvtColor(im.astype(np.uint8), cv2.COLOR_RGB2HSV).astype(np.float32)
h, s, v = hsv[..., 0] * 2, hsv[..., 1] / 255, hsv[..., 2] / 255

def px(x_m, z_m):  # world metres -> image pixel
    return 627 + x_m / 0.344, 627 - z_m / 0.344

water = (b > r + 60) & (b > g + 10) & (h > 185) & (h < 235)
# land = everything that is not water, holes (lake / river) filled after a closing that bridges the river mouth
land0 = ~water
land0 = cv2.morphologyEx(land0.astype(np.uint8), cv2.MORPH_OPEN, np.ones((5, 5), np.uint8))
lab, n = ndi.label(land0)
sizes = ndi.sum(land0, lab, range(1, n + 1))
main = lab == (1 + int(np.argmax(sizes)))          # biggest land body; offshore rocks are dropped
closed = cv2.morphologyEx(main.astype(np.uint8), cv2.MORPH_CLOSE, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (61, 61)))
land = ndi.binary_fill_holes(closed)
# inland water = water inside the filled land, without the warehouse roofs (blue roofs in the SW yard)
inland = water & land
yard = np.zeros_like(inland)
x0, y0 = px(-150, -40); x1, y1 = px(-25, -140)
yard[int(y0):int(y1), int(x0):int(x1)] = True
inland &= ~yard
inland = cv2.morphologyEx(inland.astype(np.uint8), cv2.MORPH_OPEN, np.ones((3, 3), np.uint8)).astype(bool)
lab, n = ndi.label(inland)
sizes = ndi.sum(inland, lab, range(1, n + 1))
keep = [i + 1 for i, sz in enumerate(sizes) if sz > 3000]
inland = np.isin(lab, keep)
# the two bridges and a mid-river rock are drawn over the water: a wide closing bridges those gaps
inland = cv2.morphologyEx(inland.astype(np.uint8), cv2.MORPH_CLOSE, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (61, 61))).astype(bool)
inland = ndi.binary_fill_holes(inland)
# true coastline: land minus the river mouth that the closing bridged
land_true = land & ~(water & ~inland & ~yard) | inland
land_true = ndi.binary_fill_holes(cv2.morphologyEx(land_true.astype(np.uint8), cv2.MORPH_OPEN, np.ones((7, 7), np.uint8)))

road = (r > 205) & (g > 150) & (g < 215) & (b > 85) & (b < 160) & (r - b > 70)
road &= land_true & ~inland
road = cv2.morphologyEx(road.astype(np.uint8), cv2.MORPH_OPEN, np.ones((3, 3), np.uint8)).astype(bool)
lab, n = ndi.label(road)
sizes = ndi.sum(road, lab, range(1, n + 1))
road = np.isin(lab, [i + 1 for i, sz in enumerate(sizes) if sz > 400])
road = cv2.morphologyEx(road.astype(np.uint8), cv2.MORPH_CLOSE, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (7, 7))).astype(bool)

gray = (np.abs(r - g) < 22) & (np.abs(g - b) < 26) & (v > 0.3) & (v < 0.85) & (s < 0.22)
paved = (gray | water) & yard          # concrete plus the blue warehouse roofs standing on it
paved = cv2.morphologyEx(paved.astype(np.uint8), cv2.MORPH_CLOSE, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (25, 25))).astype(bool)
paved = ndi.binary_fill_holes(paved)
lab, n = ndi.label(paved)
sizes = ndi.sum(paved, lab, range(1, n + 1))
paved = lab == (1 + int(np.argmax(sizes)))
rock = gray & land_true & ~yard
rock = cv2.morphologyEx(rock.astype(np.uint8), cv2.MORPH_CLOSE, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (9, 9))).astype(bool)
sand = (r > 200) & (g > 175) & (b > 120) & (b < 200) & (r - b > 40) & land_true
dist_coast = ndi.distance_transform_edt(land_true) * 0.344
sand &= dist_coast < 35
# beaches are wide blobs on the coast, roads are thin strokes of the same colour: an opening keeps only the beaches
sand = cv2.morphologyEx(sand.astype(np.uint8), cv2.MORPH_CLOSE, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (21, 21)))
sand = cv2.morphologyEx(sand, cv2.MORPH_OPEN, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (27, 27)))
sand = cv2.morphologyEx(sand, cv2.MORPH_CLOSE, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (15, 15))).astype(bool)

# signed distance to coast in metres (positive inland), encoded 0..255 for -40..+40 m (0.31 m steps)
sd = ndi.distance_transform_edt(land_true) - ndi.distance_transform_edt(~land_true)
sd *= 0.344
def save(name, arr, blur=0):
    a = arr.astype(np.float32)
    if blur: a = cv2.GaussianBlur(a, (0, 0), blur)
    a = cv2.resize(a, (512, 512), interpolation=cv2.INTER_AREA)
    Image.fromarray(np.clip(a * 255 + 0.5, 0, 255).astype(np.uint8)).save(os.path.join(out, name))
# darker sand on the beach has the road colour: drop road pixels on or next to the beach
road &= ~cv2.dilate(sand.astype(np.uint8), np.ones((15, 15), np.uint8)).astype(bool)
save("coast_sdf.png", np.clip((sd + 40) / 80, 0, 1), 1.5)
wd = (ndi.distance_transform_edt(~inland) - ndi.distance_transform_edt(inland)) * 0.344   # + outside water
save("water_sdf.png", np.clip((wd + 25) / 50, 0, 1), 1.0)
save("water.png", inland, 2)
save("road.png", road, 2)
save("paved.png", paved, 2)
save("rock.png", rock, 3)
save("sand.png", sand, 4)
print("land %.0f m2, inland water %.0f m2, road %.0f m2, sand %.0f m2, rock %.0f m2, paved %.0f m2" % tuple(
    m.sum() * 0.344 ** 2 for m in (land_true, inland, road, sand, rock, paved)))
if dbg:
    os.makedirs(dbg, exist_ok=True)
    vis = (im * 0.35).astype(np.uint8)
    vis[land_true] = (vis[land_true] * 0.5 + np.array([60, 110, 50]) * 0.5).astype(np.uint8)
    for m, c in ((sand, (240, 220, 150)), (rock, (130, 130, 130)), (road, (250, 160, 60)), (paved, (90, 90, 110)), (inland, (40, 140, 255))):
        vis[m] = c
    Image.fromarray(vis).save(os.path.join(dbg, "mask_debug.png"))
