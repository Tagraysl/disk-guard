using System.Drawing;
using System.Drawing.Drawing2D;

class IconMaker {
 static void Main() {
  using(var b=new Bitmap(64,64,System.Drawing.Imaging.PixelFormat.Format32bppArgb))
  using(var g=Graphics.FromImage(b)) {
   g.SmoothingMode=SmoothingMode.AntiAlias;g.Clear(Color.Transparent);
   using(var bg=new SolidBrush(Color.FromArgb(35,43,61)))g.FillEllipse(bg,3,3,58,58);
   using(var glow=new Pen(Color.FromArgb(115,220,255),5)) {
    glow.StartCap=LineCap.Round;glow.EndCap=LineCap.Round;glow.LineJoin=LineJoin.Round;
    g.DrawLines(glow,new[]{new Point(10,36),new Point(19,36),new Point(25,22),new Point(32,45),new Point(39,28),new Point(44,36),new Point(54,36)});
   }
   using(var p=new Pen(Color.White,1.4f))g.DrawEllipse(p,3,3,58,58);
   using(var ico=Icon.FromHandle(b.GetHicon())) using(var file=System.IO.File.Create("Guard.ico"))ico.Save(file);
  }
 }
}
