#!/bin/bash
os=`uname -s`
if [[ $os = "Linux" ]]; then
	g++ -Wl,-rpath -Wl,'$ORIGIN' -L. -g -o demoraw demoraw.cpp -ltoupcam -lncurses
else
	clang++ -Wl,-rpath -Wl,@executable_path -L. -g -o demoraw demoraw.cpp -ltoupcam -lncurses
fi
