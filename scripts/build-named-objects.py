#!/usr/bin/env python3
"""Notable objects: the famous things that carry no NGC / IC / Messier number,
or that people know by a name the big catalogues do not use. Quasars and
blazars, pulsars and magnetars, black holes, gravitational lenses, galaxy
groups and clusters, dwarf galaxies, record-holding stars, deep fields.
A search for "Phoenix A", "TON 618", "Vela pulsar" or "Cygnus X-1" finds
them, and they show on the sky map whatever the magnitude cap.

    python scripts/build-named-objects.py [--db path/to/dso.db]

Rows are written with catalog = 'Notable'; running again replaces them. The
table is hand-curated from SIMBAD / NED (J2000) and meant to stay short and
famous: this is not a catalogue mirror.
"""
import argparse
import sqlite3
import sys
from pathlib import Path

DEFAULT_DB = Path(__file__).resolve().parents[1] / "src" / "NINA.Polaris" / "wwwroot" / "catalogs" / "dso" / "dso.db"


def hms(h, m, s):
    return h + m / 60 + s / 3600


def dms(sign, d, m, s):
    v = d + m / 60 + s / 3600
    return -v if sign < 0 else v


def o(cid, name, common, typ, ra, dec, mag, size, con, aliases):
    return (cid, name, common, typ, ra, dec, mag, size, con, aliases)


# ---- quasars, blazars, radio galaxies ---------------------------------------
QUASARS = [
    o("3C273", "3C 273", "Brightest quasar", "Quasar",
      hms(12, 29, 6.70), dms(1, 2, 3, 8.6), 12.9, None, "Vir", ["3C273", "PGC 41121", "QSO B1226+023"]),
    o("3C48", "3C 48", "First identified quasar", "Quasar",
      hms(1, 37, 41.30), dms(1, 33, 9, 35.1), 16.2, None, "Tri", ["3C48", "QSO B0134+329"]),
    o("3C279", "3C 279", "Gamma-ray blazar", "Blazar",
      hms(12, 56, 11.17), dms(-1, 5, 47, 21.5), 17.8, None, "Vir", ["3C279", "PKS 1253-05"]),
    o("3C454.3", "3C 454.3", "Bright flaring blazar", "Blazar",
      hms(22, 53, 57.75), dms(1, 16, 8, 53.6), 16.1, None, "Peg", ["3C454.3", "PKS 2251+158"]),
    o("TON618", "TON 618", "Ultramassive black hole quasar", "Quasar",
      hms(12, 28, 24.97), dms(1, 31, 28, 37.7), 15.9, None, "CVn", ["TON618", "Ton 618", "FBQS J1228+3128", "PG 1226+317"]),
    o("S50014+81", "S5 0014+81", "One of the most luminous quasars", "Quasar",
      hms(0, 17, 8.48), dms(1, 81, 35, 8.1), 16.5, None, "Cep", ["S5 0014+813", "6C B0014+8120"]),
    o("APM08279", "APM 08279+5255", "Lensed hyperluminous quasar", "Quasar",
      hms(8, 31, 41.70), dms(1, 52, 45, 17.5), 15.2, None, "Lyn", ["APM 08279+5255", "APM08279+5255"]),
    o("Mrk421", "Markarian 421", "Nearest TeV blazar", "Blazar",
      hms(11, 4, 27.31), dms(1, 38, 12, 31.8), 13.3, None, "UMa", ["Mrk 421", "Mkn 421", "Markarian 421", "PGC 33452"]),
    o("Mrk501", "Markarian 501", "TeV blazar", "Blazar",
      hms(16, 53, 52.22), dms(1, 39, 45, 36.6), 13.8, None, "Her", ["Mrk 501", "Mkn 501", "Markarian 501", "PGC 59214"]),
    o("BLLac", "BL Lacertae", "Prototype blazar", "Blazar",
      hms(22, 2, 43.29), dms(1, 42, 16, 39.98), 14.5, None, "Lac", ["BL Lac", "BL Lacertae", "PGC 67807"]),
    o("OJ287", "OJ 287", "Binary black hole blazar", "Blazar",
      hms(8, 54, 48.87), dms(1, 20, 6, 30.6), 14.5, None, "Cnc", ["OJ287", "OJ 287", "PGC 24987"]),
    o("PKS2155", "PKS 2155-304", "Southern TeV blazar", "Blazar",
      hms(21, 58, 52.07), dms(-1, 30, 13, 32.1), 13.5, None, "PsA", ["PKS 2155-304", "PKS2155-304"]),
    o("Mrk231", "Markarian 231", "Nearest quasar, ULIRG", "Quasar",
      hms(12, 56, 14.23), dms(1, 56, 52, 25.2), 13.8, None, "UMa", ["Mrk 231", "Mkn 231", "Markarian 231", "UGC 8058", "PGC 44117"]),
    o("Mrk335", "Markarian 335", "Narrow-line Seyfert 1", "Galaxy",
      hms(0, 6, 19.54), dms(1, 20, 12, 10.5), 13.9, None, "Peg", ["Mrk 335", "Mkn 335", "Markarian 335", "PGC 1141"]),
    o("Mrk79", "Markarian 79", "Seyfert 1 galaxy", "Galaxy",
      hms(7, 42, 32.80), dms(1, 49, 48, 34.8), 14.3, None, "Lyn", ["Mrk 79", "Mkn 79", "Markarian 79", "UGC 3973"]),
    o("CygnusA", "Cygnus A", "Brightest extragalactic radio source", "Galaxy",
      hms(19, 59, 28.36), dms(1, 40, 44, 2.1), 15.1, None, "Cyg", ["Cygnus A", "Cyg A", "3C 405", "3C405"]),
    o("HerculesA", "Hercules A", "Radio galaxy with giant jets", "Galaxy",
      hms(16, 51, 8.15), dms(1, 4, 59, 33.3), 16.9, None, "Her", ["Hercules A", "Her A", "3C 348", "3C348"]),
    o("PictorA", "Pictor A", "Radio galaxy with X-ray jet", "Galaxy",
      hms(5, 19, 49.72), dms(-1, 45, 46, 43.9), 15.8, None, "Pic", ["Pictor A", "Pic A", "PKS 0518-45"]),
]

# ---- gravitational lenses ----------------------------------------------------
LENSES = [
    o("EinsteinCross", "Einstein Cross", "Quadruply lensed quasar", "Gravitational Lens",
      hms(22, 40, 30.3), dms(1, 3, 21, 31), 16.8, None, "Peg", ["Einstein Cross", "QSO 2237+0305", "Q2237+030", "Huchra's Lens", "Huchras Lens"]),
    o("TwinQuasar", "Twin Quasar", "First gravitationally lensed quasar", "Gravitational Lens",
      hms(10, 1, 20.7), dms(1, 55, 53, 49), 16.7, None, "UMa", ["Twin Quasar", "QSO 0957+561", "Q0957+561", "Double Quasar"]),
    o("Cloverleaf", "Cloverleaf Quasar", "Quadruply lensed quasar", "Gravitational Lens",
      hms(14, 15, 46.27), dms(1, 11, 29, 43.4), 17.0, None, "Boo", ["Cloverleaf", "H1413+117", "QSO H1413+117"]),
    o("CosmicHorseshoe", "Cosmic Horseshoe", "Near-complete Einstein ring", "Gravitational Lens",
      hms(11, 48, 33.15), dms(1, 19, 30, 3.5), 19.0, 0.2, "Leo", ["Cosmic Horseshoe", "SDSS J1148+1930", "SDSSJ1148+1930"]),
    o("SDSSJ1004", "SDSS J1004+4112", "Widest-separation lensed quasar", "Gravitational Lens",
      hms(10, 4, 34.9), dms(1, 41, 12, 39), 18.5, None, "UMa", ["SDSS J1004+4112", "SDSSJ1004+4112"]),
    o("Abell370", "Abell 370 arc", "Giant luminous arc, first lensing cluster", "Gravitational Lens",
      hms(2, 39, 53.1), dms(-1, 1, 34, 35), 18.0, 3.0, "Cet", ["Abell 370", "A370"]),
]

# ---- pulsars, magnetars, neutron stars ----------------------------------------
PULSARS = [
    o("CrabPulsar", "Crab Pulsar", "Pulsar in M1", "Pulsar",
      hms(5, 34, 31.94), dms(1, 22, 0, 52.1), 16.5, None, "Tau", ["Crab Pulsar", "PSR B0531+21", "PSR J0534+2200", "CM Tau"]),
    o("VelaPulsar", "Vela Pulsar", "Pulsar in the Vela SNR", "Pulsar",
      hms(8, 35, 20.61), dms(-1, 45, 10, 34.9), 23.6, None, "Vel", ["Vela Pulsar", "PSR B0833-45", "PSR J0835-4510"]),
    o("PSRB1919", "PSR B1919+21", "First pulsar discovered (CP 1919)", "Pulsar",
      hms(19, 21, 44.81), dms(1, 21, 53, 2.3), None, None, "Vul", ["PSR B1919+21", "CP 1919", "PSR J1921+2153", "LGM-1"]),
    o("HulseTaylor", "Hulse-Taylor Pulsar", "First binary pulsar", "Pulsar",
      hms(19, 15, 27.99), dms(1, 16, 6, 27.4), None, None, "Aql", ["Hulse-Taylor Pulsar", "PSR B1913+16", "PSR J1915+1606"]),
    o("DoublePulsar", "Double Pulsar", "Only known double pulsar", "Pulsar",
      hms(7, 37, 51.25), dms(-1, 30, 39, 40.7), None, None, "Pup", ["Double Pulsar", "PSR J0737-3039"]),
    o("BlackWidow", "Black Widow Pulsar", "Companion-evaporating millisecond pulsar", "Pulsar",
      hms(19, 59, 36.77), dms(1, 20, 48, 15.1), 20.0, None, "Sge", ["Black Widow Pulsar", "PSR B1957+20", "PSR J1959+2048"]),
    o("PSRJ0437", "PSR J0437-4715", "Nearest millisecond pulsar", "Pulsar",
      hms(4, 37, 15.90), dms(-1, 47, 15, 9.1), None, None, "Cae", ["PSR J0437-4715", "PSRJ0437-4715"]),
    o("Geminga", "Geminga", "Radio-quiet gamma-ray pulsar", "Pulsar",
      hms(6, 33, 54.15), dms(1, 17, 46, 12.9), 25.5, None, "Gem", ["Geminga", "PSR J0633+1746", "PSR B0630+178"]),
    o("Lich", "Lich", "PSR B1257+12, first exoplanets", "Pulsar",
      hms(13, 0, 3.58), dms(1, 12, 40, 55.6), None, None, "Vir", ["Lich", "PSR B1257+12", "PSR J1300+1240"]),
    o("PSRJ1748", "PSR J1748-2446ad", "Fastest spinning pulsar (Terzan 5)", "Pulsar",
      hms(17, 48, 4.9), dms(-1, 24, 46, 45), None, None, "Sgr", ["PSR J1748-2446ad", "Terzan 5 ad"]),
    o("SGR1806", "SGR 1806-20", "Magnetar, 2004 giant flare", "Magnetar",
      hms(18, 8, 39.34), dms(-1, 20, 24, 39.9), None, None, "Sgr", ["SGR 1806-20", "SGR1806-20"]),
    o("SGR1900", "SGR 1900+14", "Magnetar, 1998 giant flare", "Magnetar",
      hms(19, 7, 14.33), dms(1, 9, 19, 20.1), None, None, "Aql", ["SGR 1900+14", "SGR1900+14"]),
    o("ScoX1", "Scorpius X-1", "Brightest X-ray source, neutron star binary", "Neutron Star",
      hms(16, 19, 55.07), dms(-1, 15, 38, 24.8), 12.2, None, "Sco", ["Scorpius X-1", "Sco X-1", "V818 Sco"]),
    o("HerX1", "Hercules X-1", "X-ray pulsar binary", "Neutron Star",
      hms(16, 57, 49.81), dms(1, 35, 20, 32.5), 13.5, None, "Her", ["Hercules X-1", "Her X-1", "HZ Her"]),
    o("RXJ1856", "RX J1856.5-3754", "Nearest isolated neutron star", "Neutron Star",
      hms(18, 56, 35.11), dms(-1, 37, 54, 35.8), 25.7, None, "CrA", ["RX J1856.5-3754", "RXJ1856.5-3754"]),
]

# ---- black holes and X-ray binaries -----------------------------------------
BLACK_HOLES = [
    o("SgrA", "Sagittarius A*", "Galactic centre supermassive black hole", "Black Hole",
      hms(17, 45, 40.04), dms(-1, 29, 0, 28.1), None, None, "Sgr", ["Sagittarius A*", "Sgr A*", "Sgr A", "Sagittarius A", "Galactic Center", "Galactic Centre"]),
    o("M87star", "M87*", "First imaged black hole, in M87", "Black Hole",
      hms(12, 30, 49.42), dms(1, 12, 23, 28.0), 8.6, None, "Vir", ["M87*", "Virgo A", "3C 274", "NGC 4486"]),
    o("PhoenixA", "Phoenix A", "Phoenix Cluster central black hole", "Black Hole",
      hms(23, 44, 43.9), dms(-1, 42, 43, 12), 17.0, 2.0, "Phe", ["Phoenix Cluster", "SPT-CL J2344-4243", "SPT-CLJ2344-4243", "Phoenix A*", "Phoenix Galaxy"]),
    o("CygnusX1", "Cygnus X-1", "First confirmed stellar black hole", "Black Hole",
      hms(19, 58, 21.68), dms(1, 35, 12, 5.8), 8.95, None, "Cyg", ["Cygnus X-1", "Cyg X-1", "HDE 226868", "HD 226868", "V1357 Cyg"]),
    o("V404Cyg", "V404 Cygni", "Black hole X-ray nova", "Black Hole",
      hms(20, 24, 3.82), dms(1, 33, 52, 1.9), 18.4, None, "Cyg", ["V404 Cyg", "V404 Cygni", "GS 2023+338"]),
    o("GRS1915", "GRS 1915+105", "Superluminal microquasar", "Black Hole",
      hms(19, 15, 11.55), dms(1, 10, 56, 44.8), None, None, "Aql", ["GRS 1915+105", "GRS1915+105", "V1487 Aql"]),
    o("A0620", "A0620-00", "Nearest known black hole X-ray binary", "Black Hole",
      hms(6, 22, 44.50), dms(-1, 0, 20, 44.7), 18.3, None, "Mon", ["A0620-00", "A0620-00", "V616 Mon", "Nova Mon 1975"]),
    o("GaiaBH1", "Gaia BH1", "Dormant black hole with a Sun-like star", "Black Hole",
      hms(17, 28, 41.09), dms(-1, 0, 34, 51.9), 13.8, None, "Oph", ["Gaia BH1", "Gaia DR3 4373465352415301632"]),
    o("GX339", "GX 339-4", "Recurrent black hole transient", "Black Hole",
      hms(17, 2, 49.38), dms(-1, 48, 47, 23.2), 15.5, None, "Ara", ["GX 339-4", "GX339-4", "V821 Ara"]),
    o("CygX3", "Cygnus X-3", "Microquasar with Wolf-Rayet companion", "Black Hole",
      hms(20, 32, 25.78), dms(1, 40, 57, 27.9), None, None, "Cyg", ["Cygnus X-3", "Cyg X-3", "V1521 Cyg"]),
    o("SS433", "SS 433", "Microquasar with precessing jets", "Black Hole",
      hms(19, 11, 49.57), dms(1, 4, 58, 57.9), 14.2, None, "Aql", ["SS433", "SS 433", "V1343 Aql"]),
    o("LMCX1", "LMC X-1", "Black hole binary in the LMC", "Black Hole",
      hms(5, 39, 38.84), dms(-1, 69, 44, 35.5), 14.5, None, "Dor", ["LMC X-1", "LMCX-1"]),
    o("XTEJ1118", "XTE J1118+480", "Halo black hole binary", "Black Hole",
      hms(11, 18, 10.79), dms(1, 48, 2, 12.4), 18.8, None, "UMa", ["XTE J1118+480", "KV UMa"]),
]

# ---- galaxy groups and clusters ----------------------------------------------
GROUPS = [
    o("MarkarianChain", "Markarian's Chain", "Chain of galaxies in the Virgo Cluster", "Galaxy Group",
      hms(12, 27, 0), dms(1, 13, 10, 0), 9.0, 90.0, "Vir", ["Markarian's Chain", "Markarians Chain", "Markarian Chain"]),
    o("LeoTriplet", "Leo Triplet", "M65, M66 and NGC 3628", "Galaxy Group",
      hms(11, 19, 0), dms(1, 13, 12, 0), 9.0, 40.0, "Leo", ["Leo Triplet", "M66 Group"]),
    o("SeyfertsSextet", "Seyfert's Sextet", "Compact group HCG 79", "Galaxy Group",
      hms(15, 59, 11.9), dms(1, 20, 45, 31), 14.7, 1.5, "Ser", ["Seyfert's Sextet", "Seyferts Sextet", "HCG 79", "NGC 6027"]),
    o("CopelandSeptet", "Copeland Septet", "Compact group HCG 57", "Galaxy Group",
      hms(11, 37, 50), dms(1, 21, 59, 0), 14.0, 3.0, "Leo", ["Copeland Septet", "Copeland's Septet", "HCG 57", "Arp 320"]),
    o("WildsTriplet", "Wild's Triplet", "Interacting triplet Arp 248", "Galaxy Group",
      hms(11, 46, 45), dms(-1, 3, 50, 0), 14.5, 3.0, "Vir", ["Wild's Triplet", "Wilds Triplet", "Arp 248"]),
    o("GrusQuartet", "Grus Quartet", "NGC 7552, 7582, 7590, 7599", "Galaxy Group",
      hms(23, 17, 0), dms(-1, 42, 20, 0), 10.5, 40.0, "Gru", ["Grus Quartet", "Grus Triplet"]),
    o("DracoTrio", "Draco Trio", "NGC 5981, 5982, 5985", "Galaxy Group",
      hms(15, 38, 0), dms(1, 59, 20, 0), 11.0, 20.0, "Dra", ["Draco Trio", "Draco Triplet", "Draco Group"]),
    o("SculptorGroup", "Sculptor Group", "Nearest galaxy group beyond the Local Group", "Galaxy Group",
      hms(0, 47, 33), dms(-1, 25, 17, 18), 7.2, 500.0, "Scl", ["Sculptor Group", "South Polar Group"]),
    o("M81Group", "M81 Group", "M81, M82, NGC 3077 and companions", "Galaxy Group",
      hms(9, 55, 33), dms(1, 69, 3, 55), 6.9, 200.0, "UMa", ["M81 Group", "Bode's Group"]),
    o("VirgoCluster", "Virgo Cluster", "Nearest large galaxy cluster", "Galaxy Cluster",
      hms(12, 27, 0), dms(1, 12, 43, 0), 8.6, 600.0, "Vir", ["Virgo Cluster", "Virgo Galaxy Cluster"]),
    o("ComaCluster", "Coma Cluster", "Abell 1656", "Galaxy Cluster",
      hms(12, 59, 48.7), dms(1, 27, 58, 50), 11.5, 120.0, "Com", ["Coma Cluster", "Abell 1656", "A1656"]),
    o("PerseusCluster", "Perseus Cluster", "Abell 426, around NGC 1275", "Galaxy Cluster",
      hms(3, 19, 47.2), dms(1, 41, 30, 47), 11.9, 120.0, "Per", ["Perseus Cluster", "Abell 426", "A426", "Perseus A"]),
    o("HerculesCluster", "Hercules Cluster", "Abell 2151", "Galaxy Cluster",
      hms(16, 5, 15), dms(1, 17, 44, 55), 13.0, 60.0, "Her", ["Hercules Cluster", "Abell 2151", "A2151"]),
    o("FornaxCluster", "Fornax Cluster", "Second richest cluster within 100 million ly", "Galaxy Cluster",
      hms(3, 38, 30), dms(-1, 35, 27, 0), 9.0, 300.0, "For", ["Fornax Cluster", "Fornax Galaxy Cluster"]),
    o("CentaurusCluster", "Centaurus Cluster", "Abell 3526, around NGC 4696", "Galaxy Cluster",
      hms(12, 48, 51), dms(-1, 41, 18, 21), 11.0, 90.0, "Cen", ["Centaurus Cluster", "Abell 3526", "A3526"]),
    o("NormaCluster", "Norma Cluster", "Abell 3627, at the Great Attractor", "Galaxy Cluster",
      hms(16, 15, 32.8), dms(-1, 60, 53, 30), 13.0, 60.0, "Nor", ["Norma Cluster", "Abell 3627", "A3627", "Great Attractor"]),
    o("AntliaCluster", "Antlia Cluster", "Abell S0636", "Galaxy Cluster",
      hms(10, 30, 0), dms(-1, 35, 19, 0), 11.5, 60.0, "Ant", ["Antlia Cluster", "Abell S636", "AS636"]),
    o("BulletCluster", "Bullet Cluster", "Colliding clusters, dark matter evidence", "Galaxy Cluster",
      hms(6, 58, 37.9), dms(-1, 55, 57, 0), 18.0, 5.0, "Car", ["Bullet Cluster", "1E 0657-56", "1E 0657-558", "1E0657-56"]),
    o("ElGordo", "El Gordo", "Most massive distant cluster known", "Galaxy Cluster",
      hms(1, 2, 52.5), dms(-1, 49, 14, 58), 19.0, 3.0, "Phe", ["El Gordo", "ACT-CL J0102-4915", "ACT-CLJ0102-4915"]),
    o("PandorasCluster", "Pandora's Cluster", "Abell 2744, merging clusters", "Galaxy Cluster",
      hms(0, 14, 19.5), dms(-1, 30, 23, 19), 17.0, 5.0, "Scl", ["Pandora's Cluster", "Pandoras Cluster", "Abell 2744", "A2744"]),
    o("MACS0416", "MACS J0416.1-2403", "Hubble Frontier Field cluster", "Galaxy Cluster",
      hms(4, 16, 8.9), dms(-1, 24, 4, 28), 18.0, 3.0, "Eri", ["MACS J0416.1-2403", "MACSJ0416"]),
]

# ---- galaxies and dwarfs -----------------------------------------------------
GALAXIES = [
    o("LMC", "Large Magellanic Cloud", "Nearest large satellite galaxy", "Galaxy",
      hms(5, 23, 34.5), dms(-1, 69, 45, 22), 0.9, 645.0, "Dor", ["Large Magellanic Cloud", "LMC", "Nubecula Major", "PGC 17223"]),
    o("HoagsObject", "Hoag's Object", "Perfect ring galaxy", "Galaxy",
      hms(15, 17, 14.4), dms(1, 21, 35, 8), 16.0, 0.3, "Ser", ["Hoag's Object", "Hoags Object", "Hoag Object", "PGC 54559"]),
    o("Cartwheel", "Cartwheel Galaxy", "Ring galaxy from a head-on collision", "Galaxy",
      hms(0, 37, 41.1), dms(-1, 33, 42, 59), 15.2, 1.1, "Scl", ["Cartwheel Galaxy", "ESO 350-40", "PGC 2248"]),
    o("Maffei1", "Maffei 1", "Nearby giant elliptical behind the Milky Way", "Galaxy",
      hms(2, 36, 35.6), dms(1, 59, 39, 19), 11.1, 3.4, "Cas", ["Maffei 1", "Maffei1", "PGC 9892"]),
    o("Maffei2", "Maffei 2", "Obscured spiral near Maffei 1", "Galaxy",
      hms(2, 41, 55.0), dms(1, 59, 36, 15), 16.0, 5.8, "Cas", ["Maffei 2", "Maffei2", "PGC 10217"]),
    o("Dwingeloo1", "Dwingeloo 1", "Barred spiral found by radio survey", "Galaxy",
      hms(2, 56, 51.9), dms(1, 58, 54, 42), 14.0, 4.2, "Cas", ["Dwingeloo 1", "Dwingeloo1", "PGC 101304"]),
    o("Circinus", "Circinus Galaxy", "Nearest Seyfert 2, hidden by the Milky Way", "Galaxy",
      hms(14, 13, 9.9), dms(-1, 65, 20, 21), 12.1, 6.9, "Cir", ["Circinus Galaxy", "ESO 97-G13", "PGC 50779"]),
    o("Malin1", "Malin 1", "Giant low-surface-brightness galaxy", "Galaxy",
      hms(12, 36, 59.35), dms(1, 14, 19, 49.3), 16.0, 4.0, "Com", ["Malin 1", "Malin1", "PGC 42102"]),
    o("FornaxDwarf", "Fornax Dwarf", "Dwarf spheroidal satellite of the Milky Way", "Dwarf Galaxy",
      hms(2, 39, 59.3), dms(-1, 34, 26, 57), 9.3, 17.0, "For", ["Fornax Dwarf", "Fornax dSph", "PGC 10074"]),
    o("SculptorDwarf", "Sculptor Dwarf", "First dwarf spheroidal discovered", "Dwarf Galaxy",
      hms(1, 0, 9.4), dms(-1, 33, 42, 33), 10.1, 40.0, "Scl", ["Sculptor Dwarf", "Sculptor dSph", "PGC 3589"]),
    o("DracoDwarf", "Draco Dwarf", "Dark-matter dominated satellite", "Dwarf Galaxy",
      hms(17, 20, 12.4), dms(1, 57, 54, 55), 10.9, 35.0, "Dra", ["Draco Dwarf", "Draco dSph", "UGC 10822", "PGC 60095"]),
    o("UrsaMinorDwarf", "Ursa Minor Dwarf", "Satellite of the Milky Way", "Dwarf Galaxy",
      hms(15, 9, 8.5), dms(1, 67, 13, 21), 10.6, 30.0, "UMi", ["Ursa Minor Dwarf", "UMi dSph", "UGC 9749", "PGC 54074"]),
    o("LeoI", "Leo I", "Dwarf spheroidal near Regulus", "Dwarf Galaxy",
      hms(10, 8, 28.1), dms(1, 12, 18, 23), 11.2, 10.0, "Leo", ["Leo I", "Leo I Dwarf", "UGC 5470", "PGC 29488", "Regulus Dwarf"]),
    o("LeoII", "Leo II", "Dwarf spheroidal", "Dwarf Galaxy",
      hms(11, 13, 28.8), dms(1, 22, 9, 6), 12.6, 12.0, "Leo", ["Leo II", "Leo II Dwarf", "UGC 6253", "PGC 34176"]),
    o("SagDwarf", "Sagittarius Dwarf", "Satellite being torn apart by the Milky Way", "Dwarf Galaxy",
      hms(18, 55, 19.5), dms(-1, 30, 32, 43), 4.5, 450.0, "Sgr", ["Sagittarius Dwarf", "Sgr dSph", "Sagittarius Dwarf Spheroidal"]),
    o("WLM", "Wolf-Lundmark-Melotte", "Isolated Local Group dwarf", "Dwarf Galaxy",
      hms(0, 1, 58.2), dms(-1, 15, 27, 39), 11.0, 11.5, "Cet", ["Wolf-Lundmark-Melotte", "WLM", "DDO 221", "UGCA 444", "PGC 143"]),
    o("SextansA", "Sextans A", "Local Group dwarf irregular", "Dwarf Galaxy",
      hms(10, 11, 0.8), dms(-1, 4, 41, 34), 11.9, 5.9, "Sex", ["Sextans A", "UGCA 205", "DDO 75", "PGC 29653"]),
    o("SextansB", "Sextans B", "Local Group dwarf irregular", "Dwarf Galaxy",
      hms(10, 0, 0.1), dms(1, 5, 19, 56), 11.9, 5.1, "Sex", ["Sextans B", "UGC 5373", "DDO 70", "PGC 28913"]),
    o("PhoenixDwarf", "Phoenix Dwarf", "Local Group transition dwarf", "Dwarf Galaxy",
      hms(1, 51, 6.3), dms(-1, 44, 26, 41), 13.1, 4.9, "Phe", ["Phoenix Dwarf", "Phoenix Dwarf Galaxy", "PGC 6830"]),
    o("Segue1", "Segue 1", "Faintest, most dark-matter dominated galaxy", "Dwarf Galaxy",
      hms(10, 7, 4.0), dms(1, 16, 4, 55), 13.8, 4.4, "Leo", ["Segue 1", "Segue1"]),
    o("HannysVoorwerp", "Hanny's Voorwerp", "Quasar ionization echo near IC 2497", "Nebula",
      hms(9, 41, 4.1), dms(1, 34, 43, 58), 19.0, 0.5, "LMi", ["Hanny's Voorwerp", "Hannys Voorwerp", "Voorwerp", "SDSS J094103.80+344334.2"]),
]

# ---- supernova remnants and nebulae with no catalogue number ---------------
REMNANTS = [
    o("KeplerSNR", "Kepler's Supernova", "SN 1604 remnant", "Supernova Remnant",
      hms(17, 30, 42), dms(-1, 21, 29, 0), None, 4.0, "Oph", ["Kepler's Supernova", "Keplers Supernova", "SN 1604", "SN1604", "Kepler SNR", "G4.5+6.8", "V843 Oph"]),
    o("TychoSNR", "Tycho's Supernova", "SN 1572 remnant", "Supernova Remnant",
      hms(0, 25, 18), dms(1, 64, 9, 0), None, 8.0, "Cas", ["Tycho's Supernova", "Tychos Supernova", "SN 1572", "SN1572", "Tycho SNR", "3C 10", "3C10", "G120.1+1.4"]),
    o("CasA", "Cassiopeia A", "Youngest known Galactic supernova remnant", "Supernova Remnant",
      hms(23, 23, 26), dms(1, 58, 48, 0), None, 5.0, "Cas", ["Cassiopeia A", "Cas A", "3C 461", "3C461", "G111.7-2.1"]),
    o("SN1006", "SN 1006 remnant", "Brightest recorded supernova", "Supernova Remnant",
      hms(15, 2, 50), dms(-1, 41, 56, 0), None, 30.0, "Lup", ["SN 1006", "SN1006", "PKS 1459-41", "G327.6+14.6"]),
    o("SN1987A", "SN 1987A", "Nearest modern supernova, in the LMC", "Supernova Remnant",
      hms(5, 35, 28.0), dms(-1, 69, 16, 11.1), None, 0.03, "Dor", ["SN 1987A", "SN1987A"]),
    o("GumNebula", "Gum Nebula", "Vast ancient supernova remnant", "Supernova Remnant",
      hms(8, 30, 0), dms(-1, 45, 0, 0), None, 2160.0, "Vel", ["Gum Nebula", "Gum 12"]),
    o("PuppisA", "Puppis A", "Supernova remnant", "Supernova Remnant",
      hms(8, 24, 7), dms(-1, 42, 59, 48), None, 60.0, "Pup", ["Puppis A", "Pup A", "G260.4-3.4"]),
    o("W49B", "W49B", "Gamma-ray burst remnant candidate", "Supernova Remnant",
      hms(19, 11, 9), dms(1, 9, 6, 24), None, 4.0, "Aql", ["W49B", "W 49B", "G43.3-0.2"]),
    o("Boomerang", "Boomerang Nebula", "Coldest known place in the universe", "Nebula",
      hms(12, 44, 45.45), dms(-1, 54, 31, 11.4), 13.0, 1.4, "Cen", ["Boomerang Nebula", "Bow Tie Nebula", "ESO 172-7", "Centaurus Bipolar Nebula"]),
    o("RedRectangle", "Red Rectangle", "Protoplanetary nebula around HD 44179", "Nebula",
      hms(6, 19, 58.22), dms(-1, 10, 38, 14.7), 9.0, 0.6, "Mon", ["Red Rectangle", "Red Rectangle Nebula", "HD 44179"]),
    o("EggNebula", "Egg Nebula", "Protoplanetary nebula", "Nebula",
      hms(21, 2, 18.75), dms(1, 36, 41, 37.8), 14.0, 0.5, "Cyg", ["Egg Nebula", "CRL 2688", "RAFGL 2688", "V1610 Cyg"]),
    o("RedSquare", "Red Square Nebula", "Bipolar nebula around MWC 922", "Nebula",
      hms(18, 21, 16.06), dms(-1, 13, 1, 25.7), 10.0, 0.1, "Ser", ["Red Square Nebula", "MWC 922"]),
    o("Calabash", "Calabash Nebula", "Rotten Egg Nebula, OH 231.8+4.2", "Nebula",
      hms(7, 42, 16.83), dms(-1, 14, 42, 52.1), 9.5, 1.0, "Pup", ["Calabash Nebula", "Rotten Egg Nebula", "OH 231.8+4.2", "OH231.8+4.2"]),
    o("Homunculus", "Homunculus Nebula", "Ejecta of Eta Carinae", "Nebula",
      hms(10, 45, 3.59), dms(-1, 59, 41, 4.3), 4.5, 0.3, "Car", ["Homunculus Nebula", "Homunculus", "Eta Carinae Nebula"]),
    o("Westerhout40", "Westerhout 40", "Nearby massive star-forming region", "HII Region",
      hms(18, 31, 29), dms(-1, 2, 5, 24), None, 20.0, "Ser", ["Westerhout 40", "W40", "W 40", "Sh2-64"]),
    o("Sagittarius B2", "Sagittarius B2", "Giant molecular cloud near the Galactic centre", "Nebula",
      hms(17, 47, 20.4), dms(-1, 28, 23, 7), None, 15.0, "Sgr", ["Sagittarius B2", "Sgr B2"]),
]

# ---- stars that people ask for by fame, not by number -----------------------
STARS = [
    o("TabbysStar", "Tabby's Star", "Boyajian's Star, irregular dimming", "Variable Star",
      hms(20, 6, 15.46), dms(1, 44, 27, 24.8), 11.7, None, "Cyg", ["Tabby's Star", "Tabbys Star", "Boyajian's Star", "Boyajians Star", "KIC 8462852", "KIC8462852"]),
    o("PrzybylskisStar", "Przybylski's Star", "Most chemically peculiar star known", "Star",
      hms(11, 37, 37.04), dms(-1, 46, 42, 34.9), 8.0, None, "Cen", ["Przybylski's Star", "Przybylskis Star", "HD 101065", "HD101065", "V816 Cen"]),
    o("Methuselah", "Methuselah Star", "HD 140283, oldest known star", "Star",
      hms(15, 43, 3.10), dms(-1, 10, 56, 0.6), 7.2, None, "Lib", ["Methuselah Star", "Methuselah", "HD 140283", "HD140283"]),
    o("VYCMa", "VY Canis Majoris", "Red hypergiant, among the largest stars", "Variable Star",
      hms(7, 22, 58.33), dms(-1, 25, 46, 3.2), 7.9, None, "CMa", ["VY CMa", "VY Canis Majoris", "HD 58061", "HIP 35793"]),
    o("UYSct", "UY Scuti", "Red hypergiant", "Variable Star",
      hms(18, 27, 36.53), dms(-1, 12, 27, 58.9), 9.0, None, "Sct", ["UY Scuti", "UY Sct", "BD-12 5055"]),
    o("StephensonRZ", "Stephenson 2-18", "Candidate largest known star", "Variable Star",
      hms(18, 39, 2.37), dms(-1, 6, 5, 10.5), 15.3, None, "Sct", ["Stephenson 2-18", "Stephenson 2 DFK 1", "St2-18", "RSGC2-18"]),
    o("PistolStar", "Pistol Star", "Luminous blue variable at the Galactic centre", "Variable Star",
      hms(17, 46, 15.24), dms(-1, 28, 50, 3.6), None, None, "Sgr", ["Pistol Star", "V4647 Sgr"]),
    o("Wolf359", "Wolf 359", "Red dwarf, fifth nearest star", "Star",
      hms(10, 56, 28.86), dms(1, 7, 0, 52.8), 13.5, None, "Leo", ["Wolf 359", "Wolf359", "CN Leo", "Gliese 406", "GJ 406"]),
    o("Lalande21185", "Lalande 21185", "Red dwarf, sixth nearest star system", "Star",
      hms(11, 3, 20.19), dms(1, 35, 58, 11.6), 7.5, None, "UMa", ["Lalande 21185", "HD 95735", "Gliese 411", "GJ 411"]),
    o("Luyten726", "Luyten 726-8", "UV Ceti, flare star prototype", "Variable Star",
      hms(1, 39, 1.54), dms(-1, 17, 57, 1.8), 12.5, None, "Cet", ["Luyten 726-8", "UV Ceti", "UV Cet", "BL Ceti", "Gliese 65", "GJ 65"]),
    o("Ross128", "Ross 128", "Nearby red dwarf with a temperate planet", "Star",
      hms(11, 47, 44.40), dms(1, 0, 48, 16.4), 11.1, None, "Vir", ["Ross 128", "Ross128", "FI Vir", "Gliese 447", "GJ 447"]),
    o("Ross248", "Ross 248", "Red dwarf, future nearest star", "Star",
      hms(23, 41, 54.99), dms(1, 44, 10, 40.8), 12.3, None, "And", ["Ross 248", "Ross248", "HH And", "Gliese 905", "GJ 905"]),
    o("TeegardensStar", "Teegarden's Star", "Nearby red dwarf with planets", "Star",
      hms(2, 53, 0.89), dms(1, 16, 52, 52.6), 15.1, None, "Ari", ["Teegarden's Star", "Teegardens Star", "SO 025300.5+165258"]),
    o("KapteynsStar", "Kapteyn's Star", "Halo red subdwarf", "Star",
      hms(5, 11, 40.58), dms(-1, 45, 1, 6.3), 8.9, None, "Pic", ["Kapteyn's Star", "Kapteyns Star", "HD 33793", "Gliese 191", "GJ 191", "VZ Pic"]),
    o("VanMaanen", "Van Maanen's Star", "Nearest solitary white dwarf", "White Dwarf",
      hms(0, 49, 9.90), dms(1, 5, 23, 19.0), 12.4, None, "Psc", ["Van Maanen's Star", "Van Maanens Star", "van Maanen 2", "Gliese 35", "GJ 35", "WD 0046+051"]),
    o("SiriusB", "Sirius B", "First white dwarf discovered", "White Dwarf",
      hms(6, 45, 8.92), dms(-1, 16, 42, 58.0), 8.4, None, "CMa", ["Sirius B", "alpha CMa B", "The Pup"]),
    o("Stein2051B", "Stein 2051 B", "White dwarf that lensed a background star", "White Dwarf",
      hms(4, 31, 11.5), dms(1, 58, 58, 37), 12.4, None, "Cam", ["Stein 2051 B", "Stein 2051", "Gliese 169.1 B"]),
    o("Luhman16", "Luhman 16", "Nearest brown dwarfs", "Brown Dwarf",
      hms(10, 49, 18.9), dms(-1, 53, 19, 10), 16.2, None, "Vel", ["Luhman 16", "Luhman16", "WISE J104915.57-531906.1", "WISE 1049-5319"]),
    o("WISE0855", "WISE 0855-0714", "Coldest known brown dwarf", "Brown Dwarf",
      hms(8, 55, 10.83), dms(-1, 7, 14, 42.5), None, None, "Hya", ["WISE 0855-0714", "WISE J085510.83-071442.5"]),
    o("TRAPPIST1", "TRAPPIST-1", "Seven Earth-sized planets", "Exoplanet Host",
      hms(23, 6, 29.37), dms(-1, 5, 2, 29.0), 18.8, None, "Aqr", ["TRAPPIST-1", "TRAPPIST1", "2MASS J23062928-0502285"]),
    o("Gliese581", "Gliese 581", "Multi-planet red dwarf", "Exoplanet Host",
      hms(15, 19, 26.83), dms(-1, 7, 43, 20.2), 10.6, None, "Lib", ["Gliese 581", "GJ 581", "HO Lib"]),
    o("Gliese667C", "Gliese 667 C", "Red dwarf with habitable-zone planets", "Exoplanet Host",
      hms(17, 18, 57.16), dms(-1, 34, 59, 23.1), 10.2, None, "Sco", ["Gliese 667 C", "GJ 667 C", "Gliese 667C"]),
    o("LHS1140", "LHS 1140", "Rocky habitable-zone super-Earth host", "Exoplanet Host",
      hms(0, 44, 59.33), dms(-1, 15, 16, 17.5), 14.2, None, "Cet", ["LHS 1140", "LHS1140", "GJ 3053"]),
    o("Kepler186", "Kepler-186", "First Earth-sized habitable-zone planet host", "Exoplanet Host",
      hms(19, 54, 36.65), dms(1, 43, 57, 18.1), 14.6, None, "Cyg", ["Kepler-186", "Kepler 186", "KOI-571"]),
    o("Kepler452", "Kepler-452", "Sun-like host of an Earth cousin", "Exoplanet Host",
      hms(19, 44, 0.89), dms(1, 44, 16, 39.2), 13.4, None, "Cyg", ["Kepler-452", "Kepler 452", "KOI-7016"]),
    o("Kepler16", "Kepler-16", "Circumbinary planet host (Tatooine)", "Exoplanet Host",
      hms(19, 16, 18.17), dms(1, 51, 45, 26.8), 12.0, None, "Cyg", ["Kepler-16", "Kepler 16", "KOI-1611"]),
    o("HD189733", "HD 189733", "Deep-blue hot Jupiter host", "Exoplanet Host",
      hms(20, 0, 43.71), dms(1, 22, 42, 39.1), 7.7, None, "Vul", ["HD 189733", "HD189733", "V452 Vul"]),
    o("HD209458", "HD 209458", "Osiris, first transiting exoplanet host", "Exoplanet Host",
      hms(22, 3, 10.77), dms(1, 18, 53, 3.5), 7.7, None, "Peg", ["HD 209458", "HD209458", "Osiris", "V376 Peg"]),
    o("WASP12", "WASP-12", "Hot Jupiter being devoured by its star", "Exoplanet Host",
      hms(6, 30, 32.79), dms(1, 29, 40, 20.3), 11.7, None, "Aur", ["WASP-12", "WASP 12"]),
    o("PDS70", "PDS 70", "Star with directly imaged forming planets", "Exoplanet Host",
      hms(14, 8, 10.15), dms(-1, 41, 23, 52.6), 12.2, None, "Cen", ["PDS 70", "PDS70", "V1032 Cen"]),
    o("TCrB", "T Coronae Borealis", "Blaze Star, recurrent nova", "Variable Star",
      hms(15, 59, 30.16), dms(1, 25, 55, 12.6), 10.2, None, "CrB", ["T CrB", "T Coronae Borealis", "Blaze Star", "HD 143454"]),
    o("RSOph", "RS Ophiuchi", "Recurrent nova", "Variable Star",
      hms(17, 50, 13.16), dms(-1, 6, 42, 28.5), 11.0, None, "Oph", ["RS Oph", "RS Ophiuchi", "HD 162214"]),
    o("V838Mon", "V838 Monocerotis", "Light-echo star", "Variable Star",
      hms(7, 4, 4.82), dms(-1, 3, 50, 50.6), 15.7, None, "Mon", ["V838 Mon", "V838 Monocerotis"]),
    o("SS Cygni", "SS Cygni", "Prototype dwarf nova", "Variable Star",
      hms(21, 42, 42.80), dms(1, 43, 35, 9.9), 8.2, None, "Cyg", ["SS Cyg", "SS Cygni"]),
    o("Kepler90", "Kepler-90", "Eight-planet system", "Exoplanet Host",
      hms(18, 57, 44.04), dms(1, 49, 18, 18.5), 14.0, None, "Dra", ["Kepler-90", "Kepler 90", "KOI-351"]),
]

# ---- fields, echoes, oddities ------------------------------------------------
OTHER = [
    o("HDF", "Hubble Deep Field", "Hubble Deep Field North", "Deep Field",
      hms(12, 36, 49.4), dms(1, 62, 12, 58), None, 2.5, "UMa", ["Hubble Deep Field", "HDF", "HDF-N", "Hubble Deep Field North"]),
    o("HUDF", "Hubble Ultra Deep Field", "Deepest visible-light image", "Deep Field",
      hms(3, 32, 39.0), dms(-1, 27, 47, 29), None, 3.0, "For", ["Hubble Ultra Deep Field", "HUDF", "Hubble Ultra-Deep Field", "Ultra Deep Field", "XDF"]),
    o("HDFS", "Hubble Deep Field South", "Southern deep field", "Deep Field",
      hms(22, 32, 56.2), dms(-1, 60, 33, 2.7), None, 2.5, "Tuc", ["Hubble Deep Field South", "HDF-S", "HDFS"]),
    o("JWSTSMACS", "SMACS 0723", "JWST first deep field cluster", "Galaxy Cluster",
      hms(7, 23, 19.5), dms(-1, 73, 27, 15.6), 17.0, 3.0, "Vol", ["SMACS 0723", "SMACS J0723.3-7327", "Webb's First Deep Field", "JWST First Deep Field"]),
    o("LockmanHole", "Lockman Hole", "Lowest hydrogen column in the sky", "Deep Field",
      hms(10, 45, 0), dms(1, 58, 0, 0), None, 60.0, "UMa", ["Lockman Hole"]),
    o("BoötesVoid", "Bootes Void", "Great Nothing, giant cosmic void", "Other",
      hms(14, 50, 0), dms(1, 46, 0, 0), None, 1500.0, "Boo", ["Bootes Void", "Boötes Void", "Great Nothing", "Great Void"]),
    o("CygnusLoopCenter", "Cygnus Loop", "Veil Nebula supernova remnant, whole loop", "Supernova Remnant",
      hms(20, 51, 0), dms(1, 30, 40, 0), 7.0, 180.0, "Cyg", ["Cygnus Loop", "Veil Nebula complex", "Sh2-103"]),
    o("Barnard68", "Barnard 68", "Dark Bok globule", "Dark Nebula",
      hms(17, 22, 38.2), dms(-1, 23, 49, 34), None, 4.0, "Oph", ["Barnard 68", "B68", "LDN 57"]),
    o("Thors Helmet", "Thor's Helmet", "NGC 2359, Wolf-Rayet bubble", "Emission Nebula",
      hms(7, 18, 30), dms(-1, 13, 13, 36), 11.5, 8.0, "CMa", ["Thor's Helmet", "Thors Helmet", "NGC 2359", "Gum 4", "Sh2-298"]),
]

# ---- historical bright novae and supernovae -----------------------------------
NOVAE = [
    o("SAnd", "S Andromedae", "SN 1885A, first supernova seen in another galaxy (M31)", "Nova",
      hms(0, 42, 43.0), dms(1, 41, 16, 4), 21.0, None, "And", ["S Andromedae", "S And", "SN 1885A", "SN1885A"]),
    o("GKPer", "GK Persei", "Nova Persei 1901, with expanding Firework Nebula", "Nova",
      hms(3, 31, 12.01), dms(1, 43, 54, 15.5), 13.0, 1.5, "Per", ["GK Per", "GK Persei", "Nova Persei 1901", "Nova Per 1901", "Firework Nebula"]),
    o("V603Aql", "V603 Aquilae", "Nova Aquilae 1918, brightest nova of the 20th century", "Nova",
      hms(18, 48, 54.64), dms(1, 0, 35, 2.9), 11.8, None, "Aql", ["V603 Aql", "V603 Aquilae", "Nova Aquilae 1918", "Nova Aql 1918"]),
    o("DQHer", "DQ Herculis", "Nova Herculis 1934, intermediate polar", "Nova",
      hms(18, 7, 30.25), dms(1, 45, 51, 32.6), 14.5, None, "Her", ["DQ Her", "DQ Herculis", "Nova Herculis 1934", "Nova Her 1934"]),
    o("CPPup", "CP Puppis", "Nova Puppis 1942", "Nova",
      hms(8, 11, 38.09), dms(-1, 35, 21, 4.9), 15.0, None, "Pup", ["CP Pup", "CP Puppis", "Nova Puppis 1942", "Nova Pup 1942"]),
    o("V1500Cyg", "V1500 Cygni", "Nova Cygni 1975, fastest bright nova", "Nova",
      hms(21, 11, 36.61), dms(1, 48, 9, 1.9), 17.0, None, "Cyg", ["V1500 Cyg", "V1500 Cygni", "Nova Cygni 1975", "Nova Cyg 1975"]),
    o("V1974Cyg", "V1974 Cygni", "Nova Cygni 1992", "Nova",
      hms(20, 30, 31.61), dms(1, 52, 37, 51.3), 16.5, None, "Cyg", ["V1974 Cyg", "V1974 Cygni", "Nova Cygni 1992", "Nova Cyg 1992"]),
    o("V339Del", "V339 Delphini", "Nova Delphini 2013", "Nova",
      hms(20, 23, 30.68), dms(1, 20, 46, 3.8), 17.0, None, "Del", ["V339 Del", "V339 Delphini", "Nova Delphini 2013", "Nova Del 2013", "PNV J20233073+2046041"]),
    o("V1405Cas", "V1405 Cassiopeiae", "Nova Cassiopeiae 2021", "Nova",
      hms(23, 24, 47.73), dms(1, 61, 11, 14.8), 15.0, None, "Cas", ["V1405 Cas", "V1405 Cassiopeiae", "Nova Cassiopeiae 2021", "Nova Cas 2021"]),
    o("V1324Sco", "V1324 Scorpii", "Nova Scorpii 2012, gamma-ray nova", "Nova",
      hms(17, 50, 53.94), dms(-1, 32, 37, 21.0), 18.0, None, "Sco", ["V1324 Sco", "V1324 Scorpii", "Nova Scorpii 2012", "Nova Sco 2012"]),
    o("USco", "U Scorpii", "Fastest recurrent nova", "Nova",
      hms(16, 22, 30.78), dms(-1, 17, 52, 42.8), 18.0, None, "Sco", ["U Sco", "U Scorpii"]),
    o("V407Cyg", "V407 Cygni", "Symbiotic nova of 2010, first gamma-ray nova", "Nova",
      hms(21, 2, 13.05), dms(1, 45, 46, 30.5), 12.0, None, "Cyg", ["V407 Cyg", "V407 Cygni", "Nova Cygni 2010"]),
    o("SN1993J", "SN 1993J", "Bright supernova in M81", "Nova",
      hms(9, 55, 24.77), dms(1, 69, 1, 13.7), 22.0, None, "UMa", ["SN 1993J", "SN1993J"]),
    o("SN2011fe", "SN 2011fe", "Nearby Type Ia supernova in M101", "Nova",
      hms(14, 3, 5.81), dms(1, 54, 16, 25.4), 22.0, None, "UMa", ["SN 2011fe", "SN2011fe", "PTF 11kly"]),
    o("SN2014J", "SN 2014J", "Type Ia supernova in M82", "Nova",
      hms(9, 55, 42.14), dms(1, 69, 40, 26.0), 22.0, None, "UMa", ["SN 2014J", "SN2014J"]),
    o("SN2023ixf", "SN 2023ixf", "Bright 2023 supernova in M101", "Nova",
      hms(14, 3, 38.56), dms(1, 54, 18, 42.0), 22.0, None, "UMa", ["SN 2023ixf", "SN2023ixf"]),
]

# ---- isolated neutron stars: the Magnificent Seven and Calvera --------------
ISOLATED_NS = [
    o("RXJ0720", "RX J0720.4-3125", "Magnificent Seven neutron star", "Neutron Star",
      hms(7, 20, 24.96), dms(-1, 31, 25, 50.1), 26.6, None, "CMa", ["RX J0720.4-3125", "RXJ0720.4-3125"]),
    o("RXJ1308", "RX J1308.6+2127", "Magnificent Seven neutron star (RBS 1223)", "Neutron Star",
      hms(13, 8, 48.27), dms(1, 21, 27, 6.8), 28.6, None, "Com", ["RX J1308.6+2127", "RBS 1223", "RBS1223"]),
    o("RXJ1605", "RX J1605.3+3249", "Magnificent Seven neutron star", "Neutron Star",
      hms(16, 5, 18.52), dms(1, 32, 49, 18.0), 27.2, None, "CrB", ["RX J1605.3+3249", "RBS 1556", "RBS1556"]),
    o("RXJ0806", "RX J0806.4-4123", "Magnificent Seven neutron star", "Neutron Star",
      hms(8, 6, 23.40), dms(-1, 41, 22, 30.9), None, None, "Pup", ["RX J0806.4-4123", "RXJ0806.4-4123"]),
    o("RXJ0420", "RX J0420.0-5022", "Magnificent Seven neutron star", "Neutron Star",
      hms(4, 20, 1.95), dms(-1, 50, 22, 48.1), None, None, "Dor", ["RX J0420.0-5022", "RXJ0420.0-5022"]),
    o("RXJ2143", "RX J2143.0+0654", "Magnificent Seven neutron star (RBS 1774)", "Neutron Star",
      hms(21, 43, 3.38), dms(1, 6, 54, 17.5), None, None, "Peg", ["RX J2143.0+0654", "RBS 1774", "RBS1774"]),
    o("Calvera", "Calvera", "Isolated neutron star far above the Galactic plane", "Neutron Star",
      hms(14, 12, 55.84), dms(1, 79, 22, 3.7), None, None, "UMi", ["Calvera", "1RXS J141256.0+792204"]),
    o("PSRJ0108", "PSR J0108-1431", "One of the nearest and faintest pulsars", "Pulsar",
      hms(1, 8, 8.35), dms(-1, 14, 31, 50.4), None, None, "Cet", ["PSR J0108-1431", "PSRJ0108-1431"]),
]

# ---- more gravitational lenses -----------------------------------------------
MORE_LENSES = [
    o("Abell2218", "Abell 2218", "Cluster with spectacular lensed arcs", "Gravitational Lens",
      hms(16, 35, 54), dms(1, 66, 13, 0), 16.0, 4.0, "Dra", ["Abell 2218", "A2218"]),
    o("CheshireCat", "Cheshire Cat", "Lensed galaxy group that looks like a smiling cat", "Gravitational Lens",
      hms(10, 38, 43.6), dms(1, 48, 49, 18), 19.0, 0.5, "UMa", ["Cheshire Cat", "SDSS J1038+4849", "SDSSJ1038+4849"]),
    o("PG1115", "PG 1115+080", "Quadruply lensed quasar", "Gravitational Lens",
      hms(11, 18, 16.95), dms(1, 7, 45, 58.2), 16.9, None, "Leo", ["PG 1115+080", "PG1115+080"]),
    o("RXJ1131", "RX J1131-1231", "Quadruply lensed quasar with a spinning black hole", "Gravitational Lens",
      hms(11, 31, 51.6), dms(-1, 12, 31, 57), 17.0, None, "Crt", ["RX J1131-1231", "RXJ1131-1231"]),
    o("DoubleRing", "SDSS J0946+1006", "Double Einstein ring", "Gravitational Lens",
      hms(9, 46, 56.68), dms(1, 10, 6, 52.8), 17.5, None, "Leo", ["SDSS J0946+1006", "SDSSJ0946+1006", "Double Einstein Ring"]),
    o("SunburstArc", "Sunburst Arc", "Brightest known lensed galaxy", "Gravitational Lens",
      hms(15, 50, 0.4), dms(-1, 78, 11, 5), 17.0, 1.0, "Aps", ["Sunburst Arc", "PSZ1 G311.65-18.48"]),
    o("CosmicEye", "Cosmic Eye", "Lensed star-forming galaxy", "Gravitational Lens",
      hms(21, 35, 12.7), dms(-1, 1, 1, 43), 20.0, None, "Aqr", ["Cosmic Eye", "LBG J213512.73-010143"]),
    o("MoltenRing", "Molten Ring", "Nearly complete Einstein ring imaged by Hubble", "Gravitational Lens",
      hms(22, 0, 58.3), dms(-1, 60, 32, 40), 19.0, None, "Ind", ["Molten Ring", "GAL-CLUS-022058s", "GAL-CLUS-022058s Ring"]),
    o("B1938", "B1938+666", "Complete Einstein ring", "Gravitational Lens",
      hms(19, 38, 25.3), dms(1, 66, 48, 53), 20.0, None, "Dra", ["B1938+666", "JVAS B1938+666"]),
    o("Abell1689", "Abell 1689", "Most massive lensing cluster with hundreds of arcs", "Gravitational Lens",
      hms(13, 11, 29.5), dms(-1, 1, 20, 17), 15.9, 4.0, "Vir", ["Abell 1689", "A1689"]),
]

OBJECTS = (QUASARS + LENSES + MORE_LENSES + PULSARS + ISOLATED_NS + BLACK_HOLES + GROUPS
           + GALAXIES + REMNANTS + NOVAE + STARS + OTHER)


def build(db_path: Path):
    ids = [r[0] for r in OBJECTS]
    dup = {i for i in ids if ids.count(i) > 1}
    if dup:
        sys.exit(f"duplicate ids: {dup}")
    conn = sqlite3.connect(str(db_path))
    cur = conn.cursor()
    old = cur.execute("SELECT id FROM objects WHERE catalog IN ('Notable', 'Named')").fetchall()
    for (oid,) in old:
        cur.execute("DELETE FROM objects_idx WHERE id = ?", (oid,))
    cur.execute("DELETE FROM objects WHERE catalog IN ('Notable', 'Named')")
    for cid, name, common, typ, ra, dec, mag, size, con, aliases in OBJECTS:
        alias_str = "|".join(dict.fromkeys(a for a in [name] + aliases if a))
        cur.execute("""INSERT INTO objects (catalog, catalog_id, name, common_name, type, ra_hours, dec_deg,
                       magnitude, size_arcmin, constellation, aliases) VALUES (?,?,?,?,?,?,?,?,?,?,?)""",
                    ("Notable", cid, name, common, typ, ra, dec, mag, size, con, alias_str))
        oid = cur.lastrowid
        cur.execute("INSERT INTO objects_idx (id, min_ra, max_ra, min_dec, max_dec) VALUES (?,?,?,?,?)",
                    (oid, ra, ra, dec, dec))
    conn.commit()
    cur.execute("ANALYZE")
    conn.commit()
    conn.execute("VACUUM")
    conn.close()
    print(f"notable objects written: {len(OBJECTS)} (removed {len(old)} previous rows)")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--db", default=str(DEFAULT_DB))
    a = ap.parse_args()
    db = Path(a.db)
    if not db.exists():
        sys.exit(f"{db} not found; run build-dso-catalog.py first")
    build(db)


if __name__ == "__main__":
    main()
