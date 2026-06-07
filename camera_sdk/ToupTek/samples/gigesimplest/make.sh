#!/bin/bash
os=`uname -s`
if [[ $os = "Linux" ]]; then
	g++ -Wl,-rpath -Wl,'$ORIGIN' -L. -g -o gigesimplest gigesimplest.cpp -ltoupcam
else
	clang++ -Wl,-rpath -Wl,@executable_path -L. -g -o gigesimplest gigesimplest.cpp -ltoupcam
fi
