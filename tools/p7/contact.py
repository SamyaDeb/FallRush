# P7: a contact sheet of a shots folder (every Kth frame from t>=FROM) for a quick review.
#   python3 tools/p7/contact.py <dir> <out.png> [from=0] [k=3] [n=12]
import sys, glob
from PIL import Image
d, out = sys.argv[1], sys.argv[2]
fr = int(sys.argv[3]) if len(sys.argv) > 3 else 0; k = int(sys.argv[4]) if len(sys.argv) > 4 else 3; n = int(sys.argv[5]) if len(sys.argv) > 5 else 12
fs = [f for f in sorted(glob.glob(d + '/t*.png')) if int(f.split('/t')[-1][:4]) >= fr][::k][:n]
W, H = 480, 270; cols = 3
sheet = Image.new('RGB', (W * cols, H * ((len(fs) + cols - 1) // cols)))
for i, f in enumerate(fs):
    sheet.paste(Image.open(f).convert('RGB').resize((W, H)), ((i % cols) * W, (i // cols) * H))
sheet.save(out); print([f.split('/')[-1] for f in fs])
