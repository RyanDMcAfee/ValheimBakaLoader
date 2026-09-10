using System;

namespace ValheimBakaLoader.Tools.Atlas
{
    /// <summary>
    /// The slice of Valheim's own FastNoise that the live Ashlands terrain
    /// needs, ported term for term from the 1.0 dedicated server's
    /// assembly_utils.dll (decomp_noise/assembly_utils.decompiled.cs, class
    /// FastNoise at :902). Only two entry points exist here because
    /// WorldGenerator.GetAshlandsHeight calls only two:
    /// <see cref="GetCellular"/> (:3283) and <see cref="GetSimplexFractal"/>
    /// (:2666).
    ///
    /// WorldGenerator configures its one shared generator exactly once
    /// (decomp_new assembly_valheim.decompiled.cs :150341-150349):
    ///   new FastNoise(worldSeed)
    ///   SetNoiseType(Cellular)                    - only steers GetNoise, which is never called
    ///   SetCellularDistanceFunction(Euclidean)
    ///   SetCellularReturnType(Distance)
    ///   SetFractalOctaves(2)
    ///   SetSeed(0)
    /// The seed handed to the constructor is overwritten by SetSeed(0) before
    /// any sample is taken and the constructor uses the seed for nothing else,
    /// so every world samples the SAME noise field: the Ashlands crack and
    /// cell pattern is seed independent in the game, and is here too.
    ///
    /// Because the distance function and return type are fixed at Euclidean
    /// and Distance, only that one branch of the game's SingleCellular switch
    /// is ported. It returns the SQUARED distance to the nearest cell point,
    /// with no square root, which is what the game consumes.
    ///
    /// Frequency stays at FastNoise's own 0.01 default (:983): WorldGenerator
    /// never calls SetFrequency, and the caller pre-multiplies its own
    /// per-octave scale instead.
    /// </summary>
    internal sealed class FastNoise
    {
        private readonly struct Float2
        {
            public readonly double X;
            public readonly double Y;

            public Float2(double x, double y)
            {
                X = x;
                Y = y;
            }
        }

        private static readonly Float2[] Grad2D = new Float2[8]
        {
            new Float2(-1.0, -1.0),
            new Float2(1.0, -1.0),
            new Float2(-1.0, 1.0),
            new Float2(1.0, 1.0),
            new Float2(0.0, -1.0),
            new Float2(-1.0, 0.0),
            new Float2(0.0, 1.0),
            new Float2(1.0, 0.0)
        };

        private static readonly Float2[] Cell2D = new Float2[256]
        {
            new Float2(-0.2700222134590149, -0.9628540873527527),
            new Float2(0.38630926609039307, -0.9223693013191223),
            new Float2(0.04444859176874161, -0.9990116953849792),
            new Float2(-0.5992523431777954, -0.8005602359771729),
            new Float2(-0.7819280028343201, 0.6233687400817871),
            new Float2(0.9464672207832336, 0.3227999210357666),
            new Float2(-0.651414692401886, -0.7587218880653381),
            new Float2(0.9378472566604614, 0.3470483720302582),
            new Float2(-0.8497875928878784, -0.5271252393722534),
            new Float2(-0.8790425658226013, 0.47674325108528137),
            new Float2(-0.8923003077507019, -0.4514423608779907),
            new Float2(-0.37984442710876465, -0.9250503778457642),
            new Float2(-0.9951651096343994, 0.09821637719869614),
            new Float2(0.7724397778511047, -0.6350880265235901),
            new Float2(0.7573283314704895, -0.6530343294143677),
            new Float2(-0.9928004741668701, -0.1197800561785698),
            new Float2(-0.05326656997203827, 0.9985803365707397),
            new Float2(0.9754253625869751, -0.22033007442951202),
            new Float2(-0.7665018439292908, 0.6422421336174011),
            new Float2(0.9916366934776306, 0.12906061112880707),
            new Float2(-0.994696855545044, 0.10285037755966187),
            new Float2(-0.5379205346107483, -0.8429955244064331),
            new Float2(0.5022815465927124, -0.8647041320800781),
            new Float2(0.45598214864730835, -0.889988899230957),
            new Float2(-0.8659130930900574, -0.5001944303512573),
            new Float2(0.0879458412528038, -0.9961252808570862),
            new Float2(-0.5051684975624084, 0.8630207180976868),
            new Float2(0.7753185033798218, -0.6315703988075256),
            new Float2(-0.6921944618225098, 0.7217110395431519),
            new Float2(-0.5191659331321716, -0.854673445224762),
            new Float2(0.8978623151779175, -0.4402764141559601),
            new Float2(-0.17067740857601166, 0.9853269457817078),
            new Float2(-0.9353430271148682, -0.3537420630455017),
            new Float2(-0.9992404580116272, 0.03896746784448624),
            new Float2(-0.28820639848709106, -0.9575682878494263),
            new Float2(-0.9663811326026917, 0.25711381435394287),
            new Float2(-0.8759714365005493, -0.4823630154132843),
            new Float2(-0.8303123116493225, -0.5572983622550964),
            new Float2(0.05110133811831474, -0.9986934661865234),
            new Float2(-0.855837345123291, -0.5172450542449951),
            new Float2(0.09887025505304337, 0.9951003193855286),
            new Float2(0.9189016222953796, 0.39448678493499756),
            new Float2(-0.24393758177757263, -0.9697909355163574),
            new Float2(-0.812140941619873, -0.5834612846374512),
            new Float2(-0.9910431504249573, 0.13354213535785675),
            new Float2(0.8492423892021179, -0.5280031561851501),
            new Float2(-0.9717838764190674, -0.23587295413017273),
            new Float2(0.9949457049369812, 0.1004142090678215),
            new Float2(0.6241065263748169, -0.7813392281532288),
            new Float2(0.6629102826118469, 0.7486988306045532),
            new Float2(-0.7197418212890625, 0.6942418217658997),
            new Float2(-0.8143370747566223, -0.5803922414779663),
            new Float2(0.10452105104923248, -0.9945226907730103),
            new Float2(-0.10659261047840118, -0.9943027496337891),
            new Float2(0.44579967856407166, -0.8951327800750732),
            new Float2(0.10554740577936172, 0.9944142699241638),
            new Float2(-0.9927902817726135, 0.11986444890499115),
            new Float2(-0.8334366679191589, 0.5526150465011597),
            new Float2(0.9115561842918396, -0.41117560863494873),
            new Float2(0.8285545110702515, -0.5599084496498108),
            new Float2(0.7217097878456116, -0.6921957731246948),
            new Float2(0.4940492808818817, -0.8694338798522949),
            new Float2(-0.3652321398258209, -0.9309164881706238),
            new Float2(-0.9696606993675232, 0.24445484578609467),
            new Float2(0.08925509452819824, -0.9960088133811951),
            new Float2(0.5354071259498596, -0.8445941209793091),
            new Float2(-0.10535761713981628, 0.9944344162940979),
            new Float2(-0.9890284538269043, 0.14772510528564453),
            new Float2(0.004856104962527752, 0.9999881982803345),
            new Float2(0.9885598421096802, 0.15082913637161255),
            new Float2(0.9286129474639893, -0.37104982137680054),
            new Float2(-0.5832393765449524, -0.8123003244400024),
            new Float2(0.30152076482772827, 0.953459620475769),
            new Float2(-0.9575110673904419, 0.28839656710624695),
            new Float2(0.9715802073478699, -0.2367105484008789),
            new Float2(0.22998179495334625, 0.97319495677948),
            new Float2(0.9557638168334961, -0.2941352128982544),
            new Float2(0.7409561276435852, 0.6715534329414368),
            new Float2(-0.9971513748168945, -0.07542631030082703),
            new Float2(0.6905710697174072, -0.7232645153999329),
            new Float2(-0.29071369767189026, -0.9568101167678833),
            new Float2(0.5912777781486511, -0.8064679503440857),
            new Float2(-0.945459246635437, -0.3257404863834381),
            new Float2(0.6664455533027649, 0.7455536723136902),
            new Float2(0.6236134767532349, 0.7817328572273254),
            new Float2(0.9126994013786316, -0.40863165259361267),
            new Float2(-0.819176197052002, 0.5735419392585754),
            new Float2(-0.8812745809555054, -0.4726046025753021),
            new Float2(0.995331346988678, 0.09651672840118408),
            new Float2(0.9855650663375854, -0.1692969650030136),
            new Float2(-0.8495981097221375, 0.5274306535720825),
            new Float2(0.6174854040145874, -0.786582350730896),
            new Float2(0.8508156538009644, 0.5254642963409424),
            new Float2(0.9985032677650452, -0.054692499339580536),
            new Float2(0.19713716208934784, -0.9803759455680847),
            new Float2(0.6607855558395386, -0.7505747079849243),
            new Float2(-0.030974941328167915, 0.9995201826095581),
            new Float2(-0.6731660962104797, 0.73949134349823),
            new Float2(-0.7195018529891968, -0.6944905519485474),
            new Float2(0.9727511405944824, 0.23185159265995026),
            new Float2(0.9997059106826782, -0.024250689893960953),
            new Float2(0.44217875599861145, -0.8969269394874573),
            new Float2(0.9981350898742676, -0.061043672263622284),
            new Float2(-0.917366087436676, -0.39804455637931824),
            new Float2(-0.8150056600570679, -0.5794529914855957),
            new Float2(-0.8789331316947937, 0.47694501280784607),
            new Float2(0.015860583633184433, 0.9998742341995239),
            new Float2(-0.8095464706420898, 0.5870558023452759),
            new Float2(-0.9165899157524109, -0.3998286724090576),
            new Float2(-0.8023542761802673, 0.5968480706214905),
            new Float2(-0.5176737904548645, 0.8555780649185181),
            new Float2(-0.8154407143592834, -0.5788405537605286),
            new Float2(0.4022010266780853, -0.9155513644218445),
            new Float2(-0.9052556753158569, -0.4248672127723694),
            new Float2(0.7317445874214172, 0.6815789937973022),
            new Float2(-0.5647632479667664, -0.8252530097961426),
            new Float2(-0.8403276205062866, -0.5420788526535034),
            new Float2(-0.9314281344413757, 0.3639252483844757),
            new Float2(0.5238198637962341, 0.8518290519714355),
            new Float2(0.7432804107666016, -0.6689800024032593),
            new Float2(-0.9853715896606445, -0.17041973769664764),
            new Float2(0.46014687418937683, 0.8878428339958191),
            new Float2(0.8258553743362427, 0.5638819336891174),
            new Float2(0.6182366013526917, 0.7859920263290405),
            new Float2(0.8331502676010132, -0.5530466437339783),
            new Float2(0.15003074705600739, 0.9886813163757324),
            new Float2(-0.6623303890228271, -0.7492119073867798),
            new Float2(-0.6685986518859863, 0.7436234354972839),
            new Float2(0.7025606036186218, 0.7116239070892334),
            new Float2(-0.5419389605522156, -0.8404178619384766),
            new Float2(-0.3388616442680359, 0.9408361911773682),
            new Float2(0.8331530094146729, 0.5530425310134888),
            new Float2(-0.29897207021713257, -0.954261839389801),
            new Float2(0.2638522982597351, 0.9645630717277527),
            new Float2(0.12410873919725418, -0.9922686219215393),
            new Float2(-0.7282649278640747, -0.6852957010269165),
            new Float2(0.6962500214576721, 0.7177993655204773),
            new Float2(-0.9183535575866699, 0.39576101303100586),
            new Float2(-0.6326102018356323, -0.774470329284668),
            new Float2(-0.9331892132759094, -0.35938552021980286),
            new Float2(-0.11537793278694153, -0.9933216571807861),
            new Float2(0.951497495174408, -0.30765655636787415),
            new Float2(-0.08987977355718613, -0.9959526062011719),
            new Float2(0.6678497195243835, 0.7442961931228638),
            new Float2(0.795240044593811, -0.6062946915626526),
            new Float2(-0.6462007164955139, -0.7631675004959106),
            new Float2(-0.27335986495018005, 0.9619118571281433),
            new Float2(0.9669589996337891, -0.25493183732032776),
            new Float2(-0.9792894721031189, 0.20246519148349762),
            new Float2(-0.5369502902030945, -0.843613862991333),
            new Float2(-0.2700364589691162, -0.9628500938415527),
            new Float2(-0.6400277018547058, 0.7683518528938293),
            new Float2(-0.785453736782074, -0.6189203858375549),
            new Float2(0.06005905568599701, -0.9981948137283325),
            new Float2(-0.024557704105973244, 0.9996984004974365),
            new Float2(-0.6598362326622009, 0.7514094710350037),
            new Float2(-0.6253894567489624, -0.7803127765655518),
            new Float2(-0.6210408806800842, -0.783778190612793),
            new Float2(0.8348888754844666, 0.5504185557365417),
            new Float2(-0.15922752022743225, 0.9872419238090515),
            new Float2(0.8367622494697571, 0.5475663542747498),
            new Float2(-0.8675754070281982, -0.497305691242218),
            new Float2(-0.20226626098155975, -0.9793305397033691),
            new Float2(0.9399189949035645, 0.34139755368232727),
            new Float2(0.9877404570579529, -0.1561049073934555),
            new Float2(-0.9034455418586731, 0.42870283126831055),
            new Float2(0.12698042392730713, -0.9919052124023438),
            new Float2(-0.38196009397506714, 0.9241788387298584),
            new Float2(0.9754626154899597, 0.22016525268554688),
            new Float2(-0.3204015791416168, -0.9472818374633789),
            new Float2(-0.987476110458374, 0.15776874125003815),
            new Float2(0.025353483855724335, -0.9996785521507263),
            new Float2(0.48351308703422546, -0.8753371238708496),
            new Float2(-0.2850799858570099, -0.9585037231445312),
            new Float2(-0.068055160343647, -0.9976815581321716),
            new Float2(-0.7885243892669678, -0.6150034666061401),
            new Float2(0.31853920221328735, -0.9479097127914429),
            new Float2(0.8880043029785156, 0.45983514189720154),
            new Float2(0.6476921439170837, -0.7619021534919739),
            new Float2(0.9820241332054138, 0.18875542283058167),
            new Float2(0.9357275366783142, -0.35272371768951416),
            new Float2(-0.8894895315170288, 0.45695552229881287),
            new Float2(0.7922791242599487, 0.6101588010787964),
            new Float2(0.7483818531036377, 0.6632681488990784),
            new Float2(-0.728892982006073, -0.6846276521682739),
            new Float2(0.8729032874107361, -0.487893283367157),
            new Float2(0.828834593296051, 0.5594937205314636),
            new Float2(0.08074566721916199, 0.9967347383499146),
            new Float2(0.9799148440361023, -0.19941650331020355),
            new Float2(-0.580730676651001, -0.8140957355499268),
            new Float2(-0.47000497579574585, -0.8826637864112854),
            new Float2(0.24094930291175842, 0.9705377221107483),
            new Float2(0.9437816739082336, -0.33056941628456116),
            new Float2(-0.8927998542785645, -0.4504535496234894),
            new Float2(-0.806962251663208, 0.5906030535697937),
            new Float2(0.06258973479270935, 0.9980393648147583),
            new Float2(-0.9312597513198853, 0.36435598134994507),
            new Float2(0.5777449607849121, 0.8162173628807068),
            new Float2(-0.33600959181785583, -0.9418585896492004),
            new Float2(0.6979320645332336, -0.7161639332771301),
            new Float2(-0.002008157316595316, -0.9999979734420776),
            new Float2(-0.18272943794727325, -0.9831632375717163),
            new Float2(-0.6523911952972412, 0.7578824162483215),
            new Float2(-0.4302626848220825, -0.9027037024497986),
            new Float2(-0.9985126256942749, -0.05452091246843338),
            new Float2(-0.01028102170675993, -0.9999471306800842),
            new Float2(-0.4946071207523346, 0.8691166639328003),
            new Float2(-0.2999350130558014, 0.9539596438407898),
            new Float2(0.8165472149848938, 0.5772786736488342),
            new Float2(0.2697460353374481, 0.9629315137863159),
            new Float2(-0.7306287288665771, -0.6827749609947205),
            new Float2(-0.7590951919555664, -0.6509796380996704),
            new Float2(-0.9070538282394409, 0.42101460695266724),
            new Float2(-0.5104861259460449, -0.8598859906196594),
            new Float2(0.861335039138794, 0.5080373287200928),
            new Float2(0.500788152217865, -0.8655698895454407),
            new Float2(-0.6541581749916077, 0.7563577890396118),
            new Float2(-0.8382755517959595, -0.5452468395233154),
            new Float2(0.6940070986747742, 0.7199681997299194),
            new Float2(0.069509357213974, 0.9975813031196594),
            new Float2(0.17029422521591187, -0.9853932857513428),
            new Float2(0.2695973217487335, 0.9629731178283691),
            new Float2(0.5519612431526184, -0.8338697552680969),
            new Float2(0.22565749287605286, -0.9742066860198975),
            new Float2(0.4215262830257416, -0.9068161845207214),
            new Float2(0.48818734288215637, -0.8727388381958008),
            new Float2(-0.3683854937553406, -0.9296731352806091),
            new Float2(-0.9825390577316284, 0.186056450009346),
            new Float2(0.8125647306442261, 0.5828710198402405),
            new Float2(0.31964609026908875, -0.9475370049476624),
            new Float2(0.9570913910865784, 0.2897862493991852),
            new Float2(-0.6876655220985413, -0.7260276079177856),
            new Float2(-0.9988771080970764, -0.04737672954797745),
            new Float2(-0.1250178962945938, 0.9921544790267944),
            new Float2(-0.8280133605003357, 0.5607083439826965),
            new Float2(0.932486355304718, -0.361205130815506),
            new Float2(0.63946533203125, 0.7688199281692505),
            new Float2(-0.016238471493124962, -0.9998681545257568),
            new Float2(-0.9955014586448669, -0.09474613517522812),
            new Float2(-0.8145331740379333, 0.5801169872283936),
            new Float2(0.4037328064441681, -0.9148769378662109),
            new Float2(0.9944263100624084, 0.10543368011713028),
            new Float2(-0.16247116029262543, 0.9867132902145386),
            new Float2(-0.9949488043785095, -0.10038387775421143),
            new Float2(-0.6995302438735962, 0.7146030068397522),
            new Float2(0.5263414978981018, -0.8502732515335083),
            new Float2(-0.5395221710205078, 0.8419713973999023),
            new Float2(0.6579370498657227, 0.7530729174613953),
            new Float2(0.014267588034272194, -0.9998981952667236),
            new Float2(-0.6734383702278137, 0.7392433285713196),
            new Float2(0.6394121050834656, -0.7688642144203186),
            new Float2(0.9211571216583252, 0.3891908526420593),
            new Float2(-0.1466372162103653, -0.9891903400421143),
            new Float2(-0.782318115234375, 0.6228790879249573),
            new Float2(-0.5039610862731934, -0.8637263774871826),
            new Float2(-0.7743120193481445, -0.6328039765357971)
        };
        /// <summary>FastNoise's own default (:983); WorldGenerator never changes it.</summary>
        private const double Frequency = 0.01;

        /// <summary>FastNoise's own m_cellularJitter default (:993), a float in the game.</summary>
        private const float CellularJitter = 0.45f;

        private int _seed;
        private int _octaves = 3;
        private readonly double _lacunarity = 2.0;
        private readonly double _gain = 0.5;
        private double _fractalBounding;

        /// <summary>Mirrors FastNoise(int seed) at :1626.</summary>
        internal FastNoise(int seed)
        {
            _seed = seed;
            CalculateFractalBounding();
        }

        /// <summary>Mirrors SetSeed at :1642.</summary>
        internal void SetSeed(int seed)
        {
            _seed = seed;
        }

        /// <summary>Mirrors SetFractalOctaves at :1662, recalculation included.</summary>
        internal void SetFractalOctaves(int octaves)
        {
            _octaves = octaves;
            CalculateFractalBounding();
        }

        /// <summary>Mirrors CalculateFractalBounding at :1762.</summary>
        private void CalculateFractalBounding()
        {
            double amp = _gain;
            double total = 1.0;
            for (int i = 1; i < _octaves; i++)
            {
                total += amp;
                amp *= _gain;
            }
            _fractalBounding = 1.0 / total;
        }

        // ---------------------------------------------------------------
        // Cellular (Euclidean distance, Distance return) - :3283, :3295
        // ---------------------------------------------------------------

        /// <summary>
        /// GetCellular(double, double) at :3283 followed by the Euclidean arm
        /// of SingleCellular at :3295. The return is the squared distance to
        /// the nearest jittered cell point, exactly as the game returns it: the
        /// game takes no square root here.
        /// </summary>
        internal double GetCellular(double x, double y)
        {
            x *= Frequency;
            y *= Frequency;

            int cellX = FastRound(x);
            int cellY = FastRound(y);
            double best = 999999.0;

            for (int ix = cellX - 1; ix <= cellX + 1; ix++)
            {
                for (int iy = cellY - 1; iy <= cellY + 1; iy++)
                {
                    Float2 offset = Cell2D[Hash2D(_seed, ix, iy) & 0xFF];
                    double dx = (double)ix - x + offset.X * (double)CellularJitter;
                    double dy = (double)iy - y + offset.Y * (double)CellularJitter;
                    double distanceSq = dx * dx + dy * dy;
                    if (distanceSq < best)
                    {
                        best = distanceSq;
                    }
                }
            }

            return best;
        }

        // ---------------------------------------------------------------
        // Simplex fractal (FBM) - :2666, :2679, :2729
        // ---------------------------------------------------------------

        /// <summary>
        /// GetSimplexFractal(double, double) at :2666. FastNoise's fractal type
        /// default is FBM (:987) and WorldGenerator never changes it, so only
        /// the FBM arm (:2679) is ported.
        /// </summary>
        internal double GetSimplexFractal(double x, double y)
        {
            x *= Frequency;
            y *= Frequency;

            int seed = _seed;
            double sum = SingleSimplex(seed, x, y);
            double amp = 1.0;
            for (int i = 1; i < _octaves; i++)
            {
                x *= _lacunarity;
                y *= _lacunarity;
                amp *= _gain;
                sum += SingleSimplex(++seed, x, y) * amp;
            }
            return sum * _fractalBounding;
        }

        /// <summary>SingleSimplex(int, double, double) at :2729.</summary>
        private static double SingleSimplex(int seed, double x, double y)
        {
            double skew = (x + y) * 0.3660254037844386;
            int i = FastFloor(x + skew);
            int j = FastFloor(y + skew);
            skew = (double)(i + j) * 0.21132486540518713;
            double originX = (double)i - skew;
            double originY = (double)j - skew;
            double x0 = x - originX;
            double y0 = y - originY;

            int i1, j1;
            if (x0 > y0)
            {
                i1 = 1;
                j1 = 0;
            }
            else
            {
                i1 = 0;
                j1 = 1;
            }

            double x1 = x0 - (double)i1 + 0.21132486540518713;
            double y1 = y0 - (double)j1 + 0.21132486540518713;
            double x2 = x0 - 1.0 + 0.42264973081037427;
            double y2 = y0 - 1.0 + 0.42264973081037427;

            double t = 0.5 - x0 * x0 - y0 * y0;
            double n0;
            if (t < 0.0)
            {
                n0 = 0.0;
            }
            else
            {
                t *= t;
                n0 = t * t * GradCoord2D(seed, i, j, x0, y0);
            }

            t = 0.5 - x1 * x1 - y1 * y1;
            double n1;
            if (t < 0.0)
            {
                n1 = 0.0;
            }
            else
            {
                t *= t;
                n1 = t * t * GradCoord2D(seed, i + i1, j + j1, x1, y1);
            }

            t = 0.5 - x2 * x2 - y2 * y2;
            double n2;
            if (t < 0.0)
            {
                n2 = 0.0;
            }
            else
            {
                t *= t;
                n2 = t * t * GradCoord2D(seed, i + 1, j + 1, x2, y2);
            }

            return 50.0 * (n0 + n1 + n2);
        }

        // ---------------------------------------------------------------
        // Shared primitives
        // ---------------------------------------------------------------

        /// <summary>FastFloor at :1718.</summary>
        private static int FastFloor(double f)
        {
            if (!(f >= 0.0))
            {
                return (int)f - 1;
            }
            return (int)f;
        }

        /// <summary>FastRound at :1728.</summary>
        private static int FastRound(double f)
        {
            if (!(f >= 0.0))
            {
                return (int)(f - 0.5);
            }
            return (int)(f + 0.5);
        }

        /// <summary>Hash2D at :1775. The int overflow is deliberate.</summary>
        private static int Hash2D(int seed, int x, int y)
        {
            unchecked
            {
                int hash = seed;
                hash ^= 1619 * x;
                hash ^= 31337 * y;
                hash = hash * hash * hash * 60493;
                return (hash >> 13) ^ hash;
            }
        }

        /// <summary>GradCoord2D at :1838. The int overflow is deliberate.</summary>
        private static double GradCoord2D(int seed, int x, int y, double xd, double yd)
        {
            unchecked
            {
                int hash = seed;
                hash ^= 1619 * x;
                hash ^= 31337 * y;
                hash = hash * hash * hash * 60493;
                hash = (hash >> 13) ^ hash;
                Float2 gradient = Grad2D[hash & 7];
                return xd * gradient.X + yd * gradient.Y;
            }
        }
    }
}
