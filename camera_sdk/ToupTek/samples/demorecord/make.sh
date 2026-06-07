#!/bin/bash
os=`uname -s`
if [[ $os = "Linux" ]]; then
	g++ -Wl,-rpath -Wl,'$ORIGIN' -L. -g -o demorecord demorecord.cpp -ltoupcam -ltoupnam
else
	clang++ -Wl,-rpath -Wl,@executable_path -L. -g -o demorecord demorecord.cpp -ltoupcam -ltoupnam
fi
