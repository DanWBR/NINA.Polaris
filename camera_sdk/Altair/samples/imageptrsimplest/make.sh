#!/bin/bash
os=`uname -s`
if [[ $os = "Linux" ]]; then
	g++ -Wl,-rpath -Wl,'$ORIGIN' -L. -g -o imageptrsimplest imageptrsimplest.cpp -laltaircam
else
	clang++ -Wl,-rpath -Wl,@executable_path -L. -g -o imageptrsimplest imageptrsimplest.cpp -laltaircam
fi
