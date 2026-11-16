"""Original vector-drawn icon family and project-owned launcher artwork."""
from functools import lru_cache
from pathlib import Path
from PIL import Image, ImageDraw, ImageTk

ASSETS = Path(__file__).resolve().parent / "assets"


def mark_image(size=256):
    # The same VR monogram used by the website, packaged for offline use.
    with Image.open(ASSETS / "vegas-reborn.png") as source:
        return source.convert("RGBA").resize((size, size), Image.Resampling.LANCZOS)



def icon_image(name, size=20, color="#b6c0b9"):
    image = Image.new("RGBA", (96,96))
    d = ImageDraw.Draw(image)
    line = lambda xy: d.line([(x*4,y*4) for x,y in xy], fill=color, width=6, joint="curve")
    rect = lambda box: d.rounded_rectangle(tuple(v*4 for v in box), radius=6, outline=color, width=6)
    if name == "home":
        line([(3,10),(12,3),(21,10)]);line([(6,9),(6,21),(18,21),(18,9)]);line([(10,21),(10,14),(14,14),(14,21)])
    elif name == "play":
        d.polygon([(7*4,4*4),(20*4,12*4),(7*4,20*4)], fill=color)
    elif name == "account":
        d.ellipse((8*4,3*4,16*4,11*4), outline=color, width=6)
        d.arc((3*4,12*4,21*4,29*4),180,360,fill=color,width=6)
        line([(4,21),(20,21)])
    elif name == "display":
        rect((2,3,22,17));line([(8,22),(16,22)]);line([(12,17),(12,22)])
    elif name == "host":
        rect((3,3,21,10));rect((3,14,21,21))
        for y in (6.5,17.5):
            d.ellipse((6*4,(y-1)*4,8*4,(y+1)*4),fill=color)
            line([(12,y),(17,y)])
    elif name == "connections":
        d.arc((1*4,8*4,15*4,20*4),55,290,fill=color,width=6)
        d.arc((9*4,4*4,23*4,16*4),235,470,fill=color,width=6)
        line([(8,15),(16,9)])
    elif name == "updates":
        d.arc((3*4,3*4,21*4,21*4),35,300,fill=color,width=6)
        line([(21,4),(21,10),(15,10)]);line([(12,7),(12,13),(16,15)])
    elif name == "folder":
        line([(3,7),(3,4),(10,4),(13,7),(21,7),(21,21),(3,21),(3,7)])
    elif name == "sync":
        d.arc((3*4,3*4,21*4,21*4),200,355,fill=color,width=6)
        d.arc((3*4,3*4,21*4,21*4),20,175,fill=color,width=6)
        line([(16,9),(21,12),(22,6)]);line([(8,15),(3,12),(2,18)])
    elif name == "arrow":
        line([(4,12),(20,12)]);line([(14,6),(20,12),(14,18)])
    elif name == "stop":
        d.rounded_rectangle((6*4,6*4,18*4,18*4),radius=4,fill=color)
    elif name == "shield":
        line([(12,2),(21,6),(20,15),(12,22),(4,15),(3,6),(12,2)])
        line([(8,12),(11,15),(17,8)])
    elif name == "external":
        line([(10,4),(4,4),(4,20),(20,20),(20,14)])
        line([(13,3),(22,3),(22,12)]);line([(22,3),(11,14)])
    elif name == "logout":
        line([(11,4),(4,4),(4,20),(11,20)]);line([(11,12),(22,12)]);line([(17,7),(22,12),(17,17)])
    else:
        raise ValueError("Unknown companion icon: " + name)
    return image.resize((size,size), Image.Resampling.LANCZOS)


class Graphics:
    """PhotoImages belong to one Tk interpreter and stay alive with the view."""
    def __init__(self, root):
        self.root, self.cache = root, {}

    def icon(self, name, size=20, color="#b6c0b9"):
        key = (name,size,color)
        if key not in self.cache:
            self.cache[key] = ImageTk.PhotoImage(icon_image(name,size,color), master=self.root)
        return self.cache[key]

    def mark(self, size=42):
        key = ("mark",size)
        if key not in self.cache:
            self.cache[key] = ImageTk.PhotoImage(mark_image(size), master=self.root)
        return self.cache[key]
