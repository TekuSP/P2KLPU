# P2KLPU .NET POC
[![.NET](https://github.com/TekuSP/P2KLPU/actions/workflows/dotnet.yml/badge.svg?branch=master)](https://github.com/TekuSP/P2KLPU/actions/workflows/dotnet.yml)
[![CodeQL](https://github.com/TekuSP/P2KLPU/actions/workflows/code2ql.yml/badge.svg?branch=master)](https://github.com/TekuSP/P2KLPU/actions/workflows/code2ql.yml)

### This tool is used for Directly Connected Palette 2/2S to your Klipper Printer, like Voron using https://www.klipper3d.org/Config_Reference.html#palette2
### It uses built.in Slicer wipe tower, there are plans to make calculation of wiping different, and this software recommending how much you need to add to not have short splices.
### It requires 3 (4 optional) changes in PrusaSlicer against P2PP which requires complete rework: 
#### A) Enable MMU (Single Extruder Multi Material in Printer settings)
##### B) If possible set everything in Printer Settings, Single Extruder MM Setup everything to 0, but this should be optional, if it does not work when you set some value, please create Issue, my goal is to strip all Prusa Commands so you have smallest setup possible
#### C) Print Settings => Output Settings => Post Processing Script => C:\Users\richa\source\repos\p2pp\dotnet-p2pp-poc\bin\Debug\net10.0\P2KLPU.exe;
##### Give it path to P2KLPU
#### D) In Printer => Custom GCode => Start GCode you add options below listed, for example my personal options are:
```
;P2KLPU PRINTERPROFILE=e666315ff39d9c78
; Printer Profile is random number you create, which represents your printer. If you omit this directive entirely, P2KLPU derives a stable profile hash from your PrusaSlicer printer profile name (printer_settings_id), so the same printer always gets the same ID automatically.
;P2KLPU SPLICEOFFSET=38
; Splice offset is offset of your mixing hotend part, if you have short hotend, you can have low offset (Almost 0), if you have long hotend, it can be up to 60. I have Dragon Water Cooled, with longer offset, 38mm works for me
; If your splices come too soon, make offset larger, if they come too late, make it smaller. Start at 25 and see. Depending on your tube length, one layer of large rectangle with 4 colors can be enough to do this.
;P2KLPU MINSTARTSPLICE=100
; This is just checking for your first splice, so it has at least 100mm, Palette 2S manual states so 85 is minimum, but I found 100mm as safe where filament does not break (It has tension from moving around Palete)
; Your value is honored as-is (no silent clamping); values below the manual minimum (85mm) produce a warning.
;P2KLPU MINSPLICE=70
; This is just checking for every remaining splice, so it is not shorter. If splice is too short, sometimes they can break inside Palette, Palette 2S Manual states 60mm minimum, I found 70 mm safe.
; Your value is honored as-is; values below the manual minimum (60mm) produce a warning.
; A splice below the configured minimum is an ERROR: by default (STRICT=1) the export fails so PrusaSlicer
; shows you the problem instead of silently producing a file with splices that can break inside the Palette.
; Add ;P2KLPU STRICT=0 to downgrade errors to warnings and write the output anyway.

;P2KLPU EXTRAENDFILAMENT=150
; This is how much extra filament there is at the end, if you have like me hotend/extruder path 100 mm, its nice to have 50mm above to grip the filament and pull it out
;P2KLPU LINEARPINGLENGTH=1000
; This is how often Palette PINGS with Klipper, Depending on your tube length, your result may wary. Too small number means overcompensating too often, too large number, means no compensation at all
;P2KLPU MATERIAL_PETG_PETG_3_-1_-5
; This says, if you meet PETG and PETG, and there are no other directives, 3 heat, -1 Compression and -5 cooling
;P2KLPU MATERIAL_IN1_IN3_2_0_-5
; This says, even if you know material mixing, if you are mixing INPUT 1 and INPUT 3, 2 heat, 0 Compression and -5 cooling
;P2KLPU MATERIAL_IN3_IN1_2_0_-5
; This says, even if you know material mixing, if you are mixing INPUT 3 and INPUT 1, 2 heat, 0 Compression and -5 cooling
;P2KLPU MATERIAL_IN4_IN3_2_0_-5
; This says, even if you know material mixing, if you are mixing INPUT 4 and INPUT 3, 2 heat, 0 Compression and -5 cooling
;P2KLPU MATERIAL_IN3_IN4_2_0_-5
; This says, even if you know material mixing, if you are mixing INPUT 3 and INPUT 4, 2 heat, 0 Compression and -5 cooling

; You can also redefine materials as PETG2, see below, but point is, I have Gray PETG in Input 1 and Blue Matte in Input 3, Yellow Matte in Input 4, even tho all are PETG, 3 and 4 mix great, 1 and 3 mix horribly, and 1 and 4 mix horribly, I am giving extra control to user to decide
```

### This script requires .NET Runtime 10.0.1 https://dotnet.microsoft.com/en-us/download/dotnet/10.0
### Output of the script looks like this:
```
=== P2KLPU Analysis ===
Display name: xxx_-_sga_top_pylons.mcf.gcode
Extrusion mode: Relative (M83)
Total positive extrusion: 116421.693 mm
RAW_MMU effective positive extrusion: 116421.693 mm
Tower effective extrusion: 32600.053 mm
Model effective extrusion: 83821.64 mm
Tower XY bounds (from ;TYPE markers): X[60.698,100.7] Y[55.336,171.396]
Splices detected: 236
Palette pings (O31) planned: 118
O31 encodes a ping location along the extruded filament.
- In Palette 2/2S connected mode, P2PP uses O31 Dxxxxxxxx where Dxxxxxxxx is the hex of the float32 bit-pattern (little-endian) representing millimeters.
- In Palette 3 mode, it can appear as O31 L<mm> mm.
Ping plan (1-based):
  #  O31              Location(mm)
  1  O31 D44757335          981.80
  2  O31 D44f5866b         1964.20
  3  O31 D453841ad         2948.10
  4  O31 D45759afe         3929.69
  5  O31 D45997819         4911.01
  6  O31 D45b821a7         5892.21
  7  O31 D45d6cdd5         6873.73
  8  O31 D45f57673         7854.81
  9  O31 D460a10f3         8836.24
 10  O31 D46196b78         9818.87
 11  O31 D4628c25a        10800.59
 12  O31 D463816bc        11781.68
 13  O31 D46476cde        12763.22
 14  O31 D4656c480        13745.13
 15  O31 D46661944        14726.32
 16  O31 D46756ed1        15707.70
 17  O31 D4682617c        16688.74
 18  O31 D468a0bc0        17669.88
 19  O31 D4691b5d8        18650.92
 20  O31 D4699609e        19632.31
 21  O31 D46a10b07        20613.51
 22  O31 D46a8b511        21594.53
 23  O31 D46b05f2b        22575.58
 24  O31 D46b8092c        23556.59
 25  O31 D46bfb343        24537.63
 26  O31 D46c75d44        25518.63
 27  O31 D46cf0948        26500.64
 28  O31 D46d6b373        27481.73
 29  O31 D46de5d7b        28462.74
 30  O31 D46e607e8        29443.95
 31  O31 D46edb1fb        30424.99
 32  O31 D46f561c1        31408.88
 33  O31 D46fd0ccd        32390.40
 34  O31 D47025b74        33371.45
 35  O31 D4706307e        34352.49
 36  O31 D470a058f        35333.56
 37  O31 D470ddaa9        36314.66
 38  O31 D4711afb5        37295.71
 39  O31 D47158505        38277.02
 40  O31 D47195ab3        39258.70
 41  O31 D471d2fc1        40239.76
 42  O31 D47210553        41221.32
 43  O31 D4724dac7        42202.78
 44  O31 D4728afc9        43183.79
 45  O31 D472c84ef        44164.93
 46  O31 D47305a02        45146.01
 47  O31 D47342f2b        46127.17
 48  O31 D47380438        47108.22
 49  O31 D473bd938        48089.22
 50  O31 D473fae58        49070.34
 51  O31 D47438382        50051.51
 52  O31 D47475893        51032.57
 53  O31 D474b2e68        52014.41
 54  O31 D474f041d        52996.11
 55  O31 D4752da42        53978.26
 56  O31 D4756af51        54959.32
 57  O31 D475a8466        55940.40
 58  O31 D475e5ad6        56922.84
 59  O31 D47622fe7        57903.90
 60  O31 D476604f6        58884.96
 61  O31 D4769da31        59866.19
 62  O31 D476dafa6        60847.65
 63  O31 D477184ac        61828.67
 64  O31 D477559ad        62809.68
 65  O31 D47792f04        63791.02
 66  O31 D477d0410        64772.06
 67  O31 D47806ca0        65753.25
 68  O31 D47825739        66734.45
 69  O31 D478441c2        67715.51
 70  O31 D47862c43        68696.53
 71  O31 D478817e3        69679.78
 72  O31 D478a0269        70660.82
 73  O31 D478becf3        71641.90
 74  O31 D478dd77e        72622.99
 75  O31 D478fc200        73604.00
 76  O31 D4791acd6        74585.67
 77  O31 D47939780        75567.00
 78  O31 D47958269        76548.82
 79  O31 D47976cea        77529.83
 80  O31 D479957b4        78511.40
 81  O31 D479b4237        79492.43
 82  O31 D479d2ce9        80473.82
 83  O31 D479f17a5        81455.29
 84  O31 D47a10292        82437.14
 85  O31 D47a2ed4f        83418.62
 86  O31 D47a4d801        84400.00
 87  O31 D47a6c282        85381.02
 88  O31 D47a8ad04        86362.03
 89  O31 D47aa97ae        87343.36
 90  O31 D47ac822e        88324.36
 91  O31 D47ae6de8        89307.81
 92  O31 D47b058b8        90289.44
 93  O31 D47b24343        91270.53
 94  O31 D47b42df2        92251.89
 95  O31 D47b61878        93232.94
 96  O31 D47b80334        94214.40
 97  O31 D47b9ee1a        95196.21
 98  O31 D47bbd8fc        96177.97
 99  O31 D47bdc3f3        97159.90
100  O31 D47bfae79        98140.94
101  O31 D47c19915        99122.16
102  O31 D47c383f6       100103.92
103  O31 D47c56ed6       101085.67
104  O31 D47c7598d       102067.10
105  O31 D47c9443c       103048.47
106  O31 D47cb2ed7       104029.68
107  O31 D47cd1983       105011.03
108  O31 D47cf0441       105992.50
109  O31 D47d0eeeb       106973.83
110  O31 D47d2d96b       107954.84
111  O31 D47d4c428       108936.31
112  O31 D47d6aeb0       109917.37
113  O31 D47d899bd       110899.48
114  O31 D47da84a3       111881.28
115  O31 D47dc6f7f       112863.00
116  O31 D47de5a1c       113844.22
117  O31 D47e044d1       114825.63
118  O31 D47e22f5b       115806.71

Splice plan (1-based inputs):
  #  From->To   Location(mm)   Length(mm)   Algo (Heat, Compression, Cooling) [h,c,k]
  1  4->1            5783.66      5783.66   3,-1,-5
  2  1->4            7084.91      1301.25   3,-1,-5
  3  4->1           11152.06      4067.14   3,-1,-5
  4  1->4           12455.33      1303.27   3,-1,-5
  5  4->1           16519.65      4064.32   3,-1,-5
  6  1->4           17828.22      1308.57   3,-1,-5
  7  4->1           18879.15      1050.93   3,-1,-5
  8  1->4           20322.88      1443.73   3,-1,-5
  9  4->1           21254.48       931.60   3,-1,-5
 10  1->3           21465.84       211.36   2,0,-5
 11  3->1           21721.28       255.44   2,0,-5
 12  1->4           21930.83       209.55   3,-1,-5
 13  4->1           22748.67       817.84   3,-1,-5
 14  1->3           22958.32       209.65   2,0,-5
 15  3->1           23210.52       252.20   2,0,-5
 16  1->4           23421.93       211.41   3,-1,-5
 17  4->1           24245.07       823.14   3,-1,-5
 18  1->3           24453.64       208.57   2,0,-5
 19  3->1           24707.96       254.32   2,0,-5
 20  1->4           24922.54       214.58   3,-1,-5
 21  4->1           25739.78       817.24   3,-1,-5
 22  1->3           25954.62       214.84   2,0,-5
 23  3->1           26211.21       256.60   2,0,-5
 24  1->4           26423.50       212.29   3,-1,-5
 25  4->1           27240.46       816.96   3,-1,-5
 26  1->3           27459.31       218.85   2,0,-5
 27  3->1           27711.50       252.20   2,0,-5
 28  1->4           27930.04       218.53   3,-1,-5
 29  4->1           28751.78       821.75   3,-1,-5
 30  1->3           28966.01       214.23   2,0,-5
 31  3->1           29220.33       254.32   2,0,-5
 32  1->4           29435.22       214.89   3,-1,-5
 33  4->1           30252.54       817.33   3,-1,-5
 34  1->3           30466.53       213.99   2,0,-5
 35  3->1           30723.13       256.60   2,0,-5
 36  1->4           30933.76       210.63   3,-1,-5
 37  4->1           31748.49       814.73   3,-1,-5
 38  1->3           31961.54       213.05   2,0,-5
 39  3->1           32213.74       252.20   2,0,-5
 40  1->4           32426.11       212.37   3,-1,-5
 41  4->1           33248.39       822.28   3,-1,-5
 42  1->3           33458.69       210.30   2,0,-5
 43  3->1           33713.01       254.32   2,0,-5
 44  1->4           33924.99       211.98   3,-1,-5
 45  4->1           34741.37       816.38   3,-1,-5
 46  1->3           34951.57       210.21   2,0,-5
 47  3->1           35208.17       256.60   2,0,-5
 48  1->4           35417.26       209.10   3,-1,-5
 49  4->1           36234.11       816.84   3,-1,-5
 50  1->3           36444.15       210.04   2,0,-5
 51  3->1           36696.35       252.20   2,0,-5
 52  1->4           36906.07       209.73   3,-1,-5
 53  4->1           37728.42       822.35   3,-1,-5
 54  1->3           37936.44       208.02   2,0,-5
 55  3->1           38190.76       254.32   2,0,-5
 56  1->4           38398.12       207.36   3,-1,-5
 57  4->1           39214.63       816.50   3,-1,-5
 58  1->3           39422.56       207.94   2,0,-5
 59  3->1           39681.95       259.39   2,0,-5
 60  1->4           39888.98       207.03   3,-1,-5
 61  4->1           40706.71       817.73   3,-1,-5
 62  1->3           40914.43       207.73   2,0,-5
 63  3->1           41180.67       266.23   2,0,-5
 64  1->4           41388.64       207.97   3,-1,-5
 65  4->1           42214.55       825.91   3,-1,-5
 66  1->3           42420.71       206.16   2,0,-5
 67  3->1           42692.80       272.09   2,0,-5
 68  1->4           42900.02       207.22   3,-1,-5
 69  4->1           43720.53       820.51   3,-1,-5
 70  1->3           43928.13       207.60   2,0,-5
 71  3->1           44209.93       281.80   2,0,-5
 72  1->4           44414.05       204.12   3,-1,-5
 73  4->1           45242.94       828.89   3,-1,-5
 74  1->3           45448.39       205.45   2,0,-5
 75  3->1           45732.90       284.51   2,0,-5
 76  1->4           45937.14       204.23   3,-1,-5
 77  4->1           46770.61       833.48   3,-1,-5
 78  1->3           46973.57       202.96   2,0,-5
 79  3->1           47259.56       285.99   2,0,-5
 80  1->4           47465.49       205.92   3,-1,-5
 81  4->1           48286.50       821.02   3,-1,-5
 82  1->3           48490.22       203.72   2,0,-5
 83  3->1           48821.95       331.73   2,0,-5
 84  1->4           49025.13       203.18   3,-1,-5
 85  4->1           49911.74       886.61   3,-1,-5
 86  1->3           50115.13       203.39   2,0,-5
 87  3->1           50434.85       319.72   2,0,-5
 88  1->4           50646.31       211.46   3,-1,-5
 89  4->1           51739.14      1092.83   3,-1,-5
 90  1->3           51949.79       210.65   2,0,-5
 91  3->1           52258.88       309.09   2,0,-5
 92  1->4           52466.79       207.91   3,-1,-5
 93  4->1           53484.31      1017.52   3,-1,-5
 94  1->3           53694.52       210.21   2,0,-5
 95  3->1           54183.85       489.33   2,0,-5
 96  1->4           54392.91       209.06   3,-1,-5
 97  4->1           55048.74       655.83   3,-1,-5
 98  1->3           55255.47       206.73   2,0,-5
 99  3->1           56052.41       796.94   2,0,-5
100  1->4           56260.43       208.02   3,-1,-5
101  4->1           56546.19       285.77   3,-1,-5
102  1->3           56749.17       202.98   2,0,-5
103  3->1           57555.38       806.21   2,0,-5
104  1->4           57756.66       201.28   3,-1,-5
105  4->1           58043.21       286.55   3,-1,-5
106  1->3           58245.95       202.74   2,0,-5
107  3->1           59024.88       778.94   2,0,-5
108  1->4           59232.97       208.09   3,-1,-5
109  4->1           59519.31       286.34   3,-1,-5
110  1->3           59727.89       208.58   2,0,-5
111  3->1           60460.91       733.03   2,0,-5
112  1->4           60669.73       208.82   3,-1,-5
113  4->1           60955.50       285.77   3,-1,-5
114  1->3           61161.95       206.46   2,0,-5
115  3->1           61896.54       734.58   2,0,-5
116  1->4           62104.97       208.44   3,-1,-5
117  4->1           62391.52       286.55   3,-1,-5
118  1->3           62615.15       223.64   2,0,-5
119  3->1           63357.75       742.60   2,0,-5
120  1->4           63559.88       202.12   3,-1,-5
121  4->1           63912.37       352.50   3,-1,-5
122  1->3           64112.75       200.37   2,0,-5
123  3->1           64858.64       745.89   2,0,-5
124  1->4           65060.63       201.99   3,-1,-5
125  4->1           65411.66       351.03   3,-1,-5
126  1->3           65613.67       202.01   2,0,-5
127  3->1           66360.41       746.74   2,0,-5
128  1->4           66560.66       200.25   3,-1,-5
129  4->1           66942.63       381.97   3,-1,-5
130  1->3           67146.92       204.29   2,0,-5
131  3->1           67958.91       811.99   2,0,-5
132  1->4           68164.58       205.67   3,-1,-5
133  4->1           68835.19       670.60   3,-1,-5
134  1->3           68989.58       154.39   2,0,-5
135  3->1           69587.84       598.26   2,0,-5
136  1->4           69739.55       151.70   3,-1,-5
137  4->1           70909.63      1170.09   3,-1,-5
138  1->3           71061.49       151.86   2,0,-5
139  3->1           71330.80       269.30   2,0,-5
140  1->4           71482.65       151.85   3,-1,-5
141  4->1           72753.59      1270.94   3,-1,-5
142  1->3           72905.32       151.73   2,0,-5
143  3->1           73152.09       246.77   2,0,-5
144  1->4           73303.87       151.78   3,-1,-5
145  4->1           74407.99      1104.12   3,-1,-5
146  1->3           74559.84       151.85   2,0,-5
147  3->1           74806.54       246.70   2,0,-5
148  1->4           74958.25       151.71   3,-1,-5
149  4->1           75930.82       972.57   3,-1,-5
150  1->3           76082.62       151.79   2,0,-5
151  3->1           76381.40       298.78   2,0,-5
152  1->4           76533.23       151.83   3,-1,-5
153  4->1           78037.54      1504.31   3,-1,-5
154  1->3           78189.24       151.70   2,0,-5
155  3->1           78518.61       329.37   2,0,-5
156  1->4           78670.41       151.80   3,-1,-5
157  4->1           80004.46      1334.05   3,-1,-5
158  1->3           80156.30       151.84   2,0,-5
159  3->1           80516.39       360.10   2,0,-5
160  1->4           80668.10       151.71   3,-1,-5
161  4->1           82187.67      1519.57   3,-1,-5
162  1->3           82339.47       151.80   2,0,-5
163  3->1           82709.13       369.66   2,0,-5
164  1->4           82860.96       151.83   3,-1,-5
165  4->1           84438.00      1577.05   3,-1,-5
166  1->3           84589.72       151.71   2,0,-5
167  3->1           85181.71       591.99   2,0,-5
168  1->4           85333.51       151.80   3,-1,-5
169  4->1           86462.88      1129.37   3,-1,-5
170  1->3           86614.70       151.83   2,0,-5
171  3->1           87178.35       563.65   2,0,-5
172  1->4           87330.06       151.71   3,-1,-5
173  4->1           88518.33      1188.27   3,-1,-5
174  1->3           88670.14       151.81   2,0,-5
175  3->1           89211.32       541.18   2,0,-5
176  1->4           89363.14       151.82   3,-1,-5
177  4->1           90233.94       870.80   3,-1,-5
178  1->3           90385.67       151.73   2,0,-5
179  3->1           90917.21       531.54   2,0,-5
180  1->4           91069.02       151.81   3,-1,-5
181  4->1           91946.40       877.38   3,-1,-5
182  1->3           92098.20       151.80   2,0,-5
183  3->1           92557.09       458.89   2,0,-5
184  1->4           92708.82       151.73   3,-1,-5
185  4->1           93529.74       820.92   3,-1,-5
186  1->3           93681.55       151.80   2,0,-5
187  3->1           93959.72       278.17   2,0,-5
188  1->4           94111.52       151.80   3,-1,-5
189  4->1           95985.58      1874.06   3,-1,-5
190  1->3           96137.31       151.73   2,0,-5
191  3->1           96528.76       391.46   2,0,-5
192  1->4           96680.56       151.80   3,-1,-5
193  4->1           98780.08      2099.52   3,-1,-5
194  1->3           98931.87       151.79   2,0,-5
195  3->1           99258.95       327.08   2,0,-5
196  1->4           99410.67       151.73   3,-1,-5
197  4->1          101520.26      2109.59   3,-1,-5
198  1->3          101672.05       151.79   2,0,-5
199  3->1          101979.69       307.64   2,0,-5
200  1->4          102131.47       151.78   3,-1,-5
201  4->1          104459.43      2327.95   3,-1,-5
202  1->3          104611.16       151.73   2,0,-5
203  3->1          104877.86       266.71   2,0,-5
204  1->4          105029.65       151.79   3,-1,-5
205  4->1          105860.49       830.84   3,-1,-5
206  1->3          106012.27       151.79   2,0,-5
207  3->1          106291.71       279.44   2,0,-5
208  1->4          106443.39       151.68   3,-1,-5
209  4->1          107394.32       950.93   3,-1,-5
210  1->3          107546.08       151.76   2,0,-5
211  3->4          107833.84       287.76   2,0,-5
212  4->3          108825.33       991.49   2,0,-5
213  3->4          109324.91       499.58   2,0,-5
214  4->3          110013.64       688.73   2,0,-5
215  3->4          110825.74       812.10   2,0,-5
216  4->3          111199.33       373.59   2,0,-5
217  3->4          111901.04       701.72   2,0,-5
218  4->3          112188.30       287.25   2,0,-5
219  3->4          112412.33       224.03   2,0,-5
220  4->3          112631.98       219.65   2,0,-5
221  3->4          112859.96       227.99   2,0,-5
222  4->3          113080.76       220.80   2,0,-5
223  3->4          113300.28       219.52   2,0,-5
224  4->3          113519.38       219.10   2,0,-5
225  3->4          113738.66       219.28   2,0,-5
226  4->3          113954.98       216.32   2,0,-5
227  3->4          114128.39       173.41   2,0,-5
228  4->3          114345.70       217.31   2,0,-5
229  3->4          114494.03       148.33   2,0,-5
230  4->3          114753.05       259.02   2,0,-5
231  3->4          114900.55       147.50   2,0,-5
232  4->3          115141.78       241.22   2,0,-5
233  3->4          115290.26       148.48   2,0,-5
234  4->3          115524.21       233.95   2,0,-5
235  3->4          115672.54       148.33   2,0,-5
236  4->3          115866.70       194.15   2,0,-5

Input usage summary:
  Input  Material   Used(mm)   Min splice(mm)   Max splice(mm)
██ DI1    PETG       24200.60           151.68          1443.73
██ DI3    PETG       24855.82           147.50           812.10
██ DI4    PETG       67403.28           194.15          5783.66

Overall splice lengths: min #231 (3->4) 147.50 mm, max #1 (4->1) 5783.66 mm


Wrote G-code: C:\Users\richa\AppData\Local\Temp\.48308_4.gcode.pp

Press any key to exit...
```

### In modern Windows Terminal it uses colors to make reading easier:
<img width="1260" height="1525" alt="image" src="https://github.com/user-attachments/assets/b7188a15-23f8-4c57-a1b6-14c2ea5060d4" />

### Please report any bugs with Github Issues, this script was heavily inspired by P2PP https://github.com/tomvandeneede/p2pp and https://github.com/vhspace/p2pp to both @tomvandeneede and @vhspace I am giving my respects to keeping this alive

### Last final warning, this code was half written by GPT5.2, I have reviewed and written large part of code myself, but the project was too huge to analyze over weekened alone.... I will work on improvements myself.

#### Foot note, for development, clone the repo, have .NET 10 SDK, and use Visual Studio Code or Visual Studio 2026 Community, no special anything required, its console app

This folder contains a .NET C# proof-of-concept for a future rewrite.

Current focus:
- Drop GUI entirely.
- Primary target: **Klipper + Palette 2/2S connected mode**.
- Keep configuration **inside the G-code** via `;P2KLPU ...` comment directives (so PrusaSlicer can drive it).
- Normalize emitted pauses (`G4`) to be more Klipper-friendly and optionally hook macros around ping blocks.

## What it does today

- Accepts PrusaSlicer post-processing style invocation: `input.gcode [output.gcode]`.
- Reads G-code, detects tool changes (`T0`, `T1`, … / `ACTIVATE_EXTRUDER`), tracks extrusion, and prints a **splice plan**.
- With `--dry-run`, writes nothing (analysis only).
- **No CLI configuration flags** besides `--dry-run` and `--verbose`.
- Supports **in-file directives** anywhere in the G-code (usually in slicer start-gcode comments), for example:
	- `;P2KLPU SPLICE_OFFSET=0`
	- `;P2KLPU DEFAULT_ALGO=10,5,3`
	- `;P2KLPU ALGO 1-2=12,7,0`
	- Material-based algorithm overrides (no `=` form; legacy style):
		- `;P2KLPU MATERIAL_DEFAULT_0_0_0` (sets `DEFAULT_ALGO`)
		- `;P2KLPU MATERIAL_PETG_PLA_3_-1_-6` (applies to any transition where `filament_type` matches)
		- `;P2KLPU MATERIAL_DI1_DI2_3_-1_-6` (directly sets the algorithm for input transition DI1 → DI2)
	- `;P2KLPU SYNC_BEFORE_G4=1` (default is on)
	- `;P2KLPU G4_ZERO_TO_M400=1` (default is on)
	- `;P2KLPU REWRITE_M0_M1=1` (default is on)
	- `;P2KLPU DROP_M0_M1_AFTER_O1=1` (default is on)
	- `;P2KLPU SYNC_PING_MACRO_OVERRIDE=MyOwnMacro` (replaces the ping-block sync barrier line)
	- `;P2KLPU PING_MACRO_BEFORE=PING_BEGIN`
	- `;P2KLPU PING_MACRO_AFTER=PING_END`
	- `;P2KLPU OCTOPRINT_STRIP_O_COMMANDS=1` (advanced; see OctoPrint section)
- Normalizes `G4`:
	- Rewrites `G4 S<seconds>` to `G4 P<milliseconds>`.
	- In Klipper mode, replaces `G4 S0` / `G4 P0` with `M400` (configurable via `G4_ZERO_TO_M400`).
	- In Klipper mode, inserts `M400` before non-zero `G4` (configurable via `SYNC_BEFORE_G4`).
- Normalizes slicer pauses:
	- Rewrites `M0`/`M1` to `PAUSE` (configurable via `REWRITE_M0_M1`).
	- Drops an `M0`/`M1` immediately after an `O1 ...` line (configurable via `DROP_M0_M1_AFTER_O1`), because Klipper’s `[palette2]` `O1` handler already pauses.
- If a ping block is detected (the file contains `; --- ... INSERT PING CODE ...`), optional macros are inserted before the ping `G4` and after the `O31` line.
- In console analysis output, `O31` pings are decoded to millimeters:
	- `O31 Dxxxxxxxx` is a legacy “hex float32” encoding of the ping position in **mm** (matches the common `hexify_float` behavior).
	- `O31 L<mm> mm` is a more human-readable form seen in some Palette 3 workflows.

RAW_MMU hardening / diagnostics:
- In `RAW_MMU` mode, the analysis prints both:
	- **Total positive extrusion** (all positive E, including toolchange prime/unload/reload), and
	- **Effective positive extrusion** (positive E excluding E-only toolchange logistics).
- When PrusaSlicer `;TYPE:...` markers are present, the analysis also breaks effective extrusion into:
	- **Tower effective extrusion** (`;TYPE:Wipe tower` / `;TYPE:Prime tower`), and
	- **Model effective extrusion** (everything else).
- The scanner prefers explicit toolchange markers when present:
	- `; CP TOOLCHANGE START/END` and `; TOOLCHANGE START/END`.
	- This makes it much more resilient to PrusaSlicer wipe tower **sparse layers** and other layout changes, because we don’t rely solely on a fixed “N lines after Tn” heuristic.

## Build / run

- `dotnet build`
- Dry-run analysis:
	- `dotnet run --project .\\P2KLPU.csproj --framework net10.0 -- input.gcode --dry-run`

## PrusaSlicer setup (post-processing)

PrusaSlicer runs post-processing scripts like:

`<script> <input.gcode> <output.gcode>`

So in PrusaSlicer you typically only enter the executable path; PrusaSlicer supplies the input/output file paths.

Windows examples:
- If you built the project and want to run via the .NET host:
	- `dotnet "C:\\path\\to\\tool\\bin\\Release\\net10.0\\P2KLPU.dll"`
- If you published a self-contained executable:
	- `"C:\\path\\to\\your-tool.exe"`

Where to put it in PrusaSlicer:
- **Print Settings → Output options → Post-processing scripts**

Notes:
- Quote paths with spaces.
- You do not need to add placeholders for input/output; PrusaSlicer appends them.

### Do I need to configure anything else in PrusaSlicer?

Usually: no — just add the post-processing script.

You *may* need additional PrusaSlicer configuration depending on the workflow:
- **RAW_MMU auto-enable**: this POC can auto-enable `RAW_MMU` when PrusaSlicer footer contains `single_extruder_multi_material = 1` and the file contains tool changes and does not already look Omega-processed.
- **OctoPrint workflow** (see below): to allow auto-detection of “printing via host”, PrusaSlicer must be configured to print to a host so it sets `SLIC3R_PP_HOST`.

## Notes

- This is *not* a full-featured tool yet: it does not implement a full purge/tower rewrite pipeline.
- Today it’s most useful as a **Klipper compatibility pass** over connected-mode output (e.g., fixing `G4 S0` and letting you hook macros around ping blocks).

Slicer feature edge cases (why Python cared more than this POC):
- Features like **variable layer height** and **combine infill** mainly matter when a processor is doing geometry- or layer-structured rewrite (e.g., purge tower geometry / per-layer heuristics), because those slicer options change layer structure and how extrusion is distributed across layers.
- This .NET POC’s RAW_MMU pipeline is primarily **stream/timeline based**: it plans splices and pings from toolchange events plus “effective positive extrusion” (E-distance excluding E-only toolchange logistics), and otherwise tries to keep print moves intact.
- Practically: variable layer height / combined infill may change *where* a ping falls along the E timeline, but they are not expected to break processing correctness the way they can for tower-geometry rewrite.

Klipper safety:
- Palette 2/2S “Omega” uses `O..` commands (non-standard G-code). On Klipper, the built-in `[palette2]` module registers the `O0..O32` commands directly (see `klippy/extras/palette2.py`), so a file like `samples/output/example_processed.gcode` can work without defining `gcode_macro O21`, etc.

Firmware flavor:
- PrusaSlicer writes `; gcode_flavor = ...` into the generated G-code (typically in the config footer).
	- If the file is marked as `klipper`, the POC enables Klipper-specific pause/sync fixes.
	- If the file is marked as a Marlin flavor, the POC is pass-through by default (no `G4` or `M0/M1` rewrites), but explicit ping-block overrides (e.g. `;P2KLPU SYNC_PING_MACRO_OVERRIDE=...`, `;P2KLPU PING_MACRO_BEFORE=...`, `;P2KLPU PING_MACRO_AFTER=...`) are still honored.

## Supported `;P2KLPU` directives

Directives are case-insensitive and can appear anywhere in the file.

General:
- `;P2KLPU RAW_MMU=0|1`
- `;P2KLPU STRICT=0|1` (default 1: error-level findings — short splices, absolute E in RAW_MMU, MMU priming enabled — fail the export with a non-zero exit code so PrusaSlicer shows them; set 0 to write output anyway)
- `;P2KLPU PRINTERPROFILE=<hex>` (Palette2 printer profile ID; omit to auto-derive a stable ID from the slicer printer profile name)
- `;P2KLPU AUTOLOADINGOFFSET=<mm>` (see note below)
- `;P2KLPU FILAMENTOVERRIDE_DI<n>=<name>` (overrides PrusaSlicer `filament_type[n-1]` for MATERIAL matching)
- `;P2KLPU FILAMENTOVERRIDE=<name>` (alias for `FILAMENTOVERRIDE_DI1`)
- `;P2KLPU EXTRAENDFILAMENT=<mm>`
- `;P2KLPU MINSTARTSPLICE=<mm>`
- `;P2KLPU MINSPLICE=<mm>`
- `;P2KLPU SPLICEOFFSET=<mm>`
- `;P2KLPU SPLICE_OFFSET=<mm>` (alias of `SPLICEOFFSET`)

About `FILAMENTOVERRIDE`:
- This changes the material name used for `MATERIAL_<FROM>_<TO>_h_c_k` matching and for Omega’s material table.
- Use it to introduce custom names like `PETG-MATTE` / `PETG2` even if PrusaSlicer’s `filament_type` is more generic.

Material aliases (Spoolman-style, recommended):
- You can attach a stable material token to the filament profile itself, so it follows whichever tool/extruder it is assigned to.
- Add `p2klpu_material` per-filament in PrusaSlicer metadata; the tool reads (first found wins): `custom_parameters_filament`, `filament_custom_variables`, `filament_notes`.
- Supported formats per filament entry:
	- JSON object (common in `custom_parameters_filament`): `{"p2klpu_material":"PETG-MATTE"}`
	- Key/value text (notes/custom variables): `p2klpu_material=PETG-MATTE`

About `AUTOLOADINGOFFSET`:
- In connected mode, Palette schedules splices/pings in terms of “mm of filament fed”.
- Some setups effectively have a fixed offset between what the Palette counts and what the printer has already consumed when printing starts (autoload / preloaded length).
- The processor uses this offset to shift Omega distances (notably `O30` splice positions, `O31` ping positions, and the total in `O1`) by the specified millimeters.

About `MINSTARTSPLICE` / `MINSPLICE`:
- These set minimum splice-length thresholds.
- Your values are honored exactly (no silent clamping); values below the Palette 2 manual minimums (85mm first / 60mm rest) additionally produce a warning.
- A computed splice below the minimum is an ERROR: with `STRICT=1` (default) the export fails so the slicer surfaces it; with `STRICT=0` it is reported and the output is still written.
- The check covers every splice including the final end-of-print splice.

About `EXTRAENDFILAMENT`:
- This adds extra “tail” filament to the final end-of-print splice (and therefore the Omega `O1` total length) so there is additional filament available after printing finishes.

About the splice schedule (important):
- The splice list always ends with a final end-of-print splice covering the last tool's segment through the end of the print plus `EXTRAENDFILAMENT`. Without it the Palette would never schedule production of the last color segment. The `O1` total equals the end of that final splice, matching what the Palette expects.
- Extrusion is accounted NET (retracts subtract, unretracts add back), matching what the Palette's encoder physically sees — so ping feedback percentages stay close to 100%.
- Arc moves (`G2`/`G3`) are fully supported in the accounting, so PrusaSlicer “Arc fitting” can stay enabled.
- A P2PP-style splice/ping summary is appended to the output G-code as comments, so you can inspect the plan after the fact even though PrusaSlicer hides console output.

About algorithm overrides and material IDs:
- The Palette 2 selects splice parameters from the `O32` table keyed by MATERIAL-ID pairs (from `O25`), not per splice. When you use input-pair overrides (`MATERIAL_DI1_DI2_...` / `MATERIAL_IN1_IN3_...` / `ALGO 1-2=...`), P2KLPU assigns each used input its OWN material ID so those per-input algorithms genuinely reach the device (with only material-name overrides, inputs sharing a material share an ID, exactly like P2PP).

Ping planning:
- `;P2KLPU PING_INTERVAL=<mm>`
- `;P2KLPU PING_MAX_INTERVAL=<mm>`
- `;P2KLPU PING_LENGTH_MULTIPLIER=<float>`

RAW_MMU toolchange stripping heuristics:
- `;P2KLPU MMU_TOOLCHANGE_WINDOW_LINES=<int>`
- `;P2KLPU MMU_E_ONLY_STRIP_THRESHOLD=<mm>` (default 15: E-only moves inside a toolchange region are stripped only when their magnitude is at least this many mm — big unload/load/ram moves go away, small retract/unretract pairs survive so the toolchange wipe still controls ooze; set 0 to strip every in-window E-only move)

Algorithm selection:
- `;P2KLPU DEFAULT_ALGO=h,c,k`
- `;P2KLPU ALGO 1-2=h,c,k` (accepts `=` or `:` between the key/value)
- `;P2KLPU MATERIAL_DEFAULT_h_c_k`
- `;P2KLPU MATERIAL_<FROM>_<TO>_h_c_k` where `<FROM>/<TO>` are either material names (from PrusaSlicer `filament_type`) or `DI1..DI4`.

Ping-block macro hooks (applies when a ping block is present or inserted):
- `;P2KLPU PING_MACRO_BEFORE=<gcode>`
- `;P2KLPU PING_MACRO_AFTER=<gcode>`
- `;P2KLPU PING_MACRO=<gcode>` (sets both before and after)
- `;P2KLPU SYNC_PING_MACRO_OVERRIDE=<gcode>` (replaces the ping-block sync line when it is a zero-length dwell)

Klipper-oriented normalization:
- `;P2KLPU SYNC_BEFORE_G4=0|1`
- `;P2KLPU G4_ZERO_TO_M400=0|1`
- `;P2KLPU REWRITE_M0_M1=0|1`
- `;P2KLPU DROP_M0_M1_AFTER_O1=0|1`

Spoolman integration:
- `;P2KLPU SPOOLMAN_SET_ACTIVE_SPOOL=0|1`
	- When enabled, the tool looks for per-filament spool IDs in PrusaSlicer metadata (`custom_parameters_filament`, `filament_custom_variables`, or `filament_notes`).
	- Supported key names inside those fields: `spoolman_id`, `spool_id`, `target_spool`.
	- When a tool change happens, it emits `SET_ACTIVE_SPOOL ID=<n>` (Klipper macro) when an ID is available for that tool.

OctoPrint/Marlin compatibility:
- `;P2KLPU OCTOPRINT_STRIP_O_COMMANDS=0|1`
	- When enabled, the processor rewrites Omega `O*` commands into comment markers (`;P2KLPU_OCTO O31 ...`) so Marlin never sees unknown `O*` commands.
	- This is intended for Marlin workflows *without* an OctoPrint Palette2 plugin.
	- If the processor detects host printing via PrusaSlicer (`SLIC3R_PP_HOST`) and the file is Marlin flavor, it assumes OctoPrint is in the loop and automatically disables this option to remain compatible with the OctoPrint Palette2 plugin (which requires real `O*` lines).


## OctoPrint (Marlin) connected-mode notes

The community OctoPrint Palette2 plugin intercepts Omega commands (`O21`, `O1`, `O31`, etc.) directly from the outgoing G-code stream.

Practical implications:
- For OctoPrint + Palette2 plugin workflows, the output must contain real `O*` lines (not commented out).
- The plugin typically suppresses `O*` lines so they are not sent to the printer, and replaces pings (`O31`) with a short dwell.

Auto-detection in this POC:
- If the slicer config indicates **Marlin** and PrusaSlicer provides `SLIC3R_PP_HOST`, the POC assumes printing via host (OctoPrint) and keeps Omega `O*` commands intact.
