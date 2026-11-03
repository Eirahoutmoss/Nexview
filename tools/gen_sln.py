"""NexView.sln üretir (Android başlık projesi hariç; o ayrı derlenir)."""
import os
projs=[("NexView.Core",r"src\NexView.Core\NexView.Core.csproj","0001"),("NexView.Codecs.Magick",r"src\NexView.Codecs.Magick\NexView.Codecs.Magick.csproj","0002"),
("NexView.App",r"src\NexView.App\NexView.App.csproj","0003"),("NexView.Platform.Windows",r"src\NexView.Platform.Windows\NexView.Platform.Windows.csproj","0005"),
("NexView.UI",r"src\NexView.UI\NexView.UI.csproj","0006"),("NexView.Mobile",r"src\NexView.Mobile\NexView.Mobile.csproj","0007"),("NexView.Studio",r"src\NexView.Studio\NexView.Studio.csproj","0008"),
("NexView.Core.Tests",r"tests\NexView.Core.Tests\NexView.Core.Tests.csproj","0004")]
root=os.path.join(os.path.dirname(os.path.abspath(__file__)),"..")
projs=[p for p in projs if os.path.exists(os.path.join(root,p[1].replace("\\",os.sep)))]
g=lambda n:"{A1B2C3D4-%s-4000-8000-00000000%s}"%(n,n)
out=["","Microsoft Visual Studio Solution File, Format Version 12.00","# Visual Studio Version 17","VisualStudioVersion = 17.0.31903.59","MinimumVisualStudioVersion = 10.0.40219.1"]
for name,path,n in projs:
    out+=['Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "%s", "%s", "%s"'%(name,path,g(n)),"EndProject"]
out+=["Global","\tGlobalSection(SolutionConfigurationPlatforms) = preSolution","\t\tDebug|Any CPU = Debug|Any CPU","\t\tRelease|Any CPU = Release|Any CPU","\tEndGlobalSection","\tGlobalSection(ProjectConfigurationPlatforms) = postSolution"]
for _,_,n in projs:
    for c in ["Debug","Release"]:
        out+=["\t\t%s.%s|Any CPU.ActiveCfg = %s|Any CPU"%(g(n),c,c),"\t\t%s.%s|Any CPU.Build.0 = %s|Any CPU"%(g(n),c,c)]
out+=["\tEndGlobalSection","EndGlobal",""]
open(os.path.join(root,"NexView.sln"),"w",newline="").write("\r\n".join(out))
print(len(projs),"proje")
